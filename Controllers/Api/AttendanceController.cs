using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers.Api
{
    /// <summary>
    /// Unified attendance API
    /// Single endpoint for attendance recording across kiosk, mobile, and device flows
    /// </summary>
    [RoutePrefix("api/attendance")]
    public class AttendanceController : Controller
    {
        private static int _activeScans;

        /// <summary>
        /// Record attendance with single frame
        /// </summary>
        [HttpPost]
        [Route("record")]
        [ValidateAntiForgeryToken]
        public ActionResult Record(HttpPostedFileBase image,
            double? lat = null, double? lon = null, double? accuracy = null,
            string deviceToken = null,
            int? faceX = null, int? faceY = null, int? faceW = null, int? faceH = null)
        {
            var requestedAtUtc = DateTime.UtcNow;

            // Rate limiting check
            var activeScans = Interlocked.Increment(ref _activeScans);
            try
            {
                var maxConcurrent = ConfigurationService.GetInt("Kiosk:MaxConcurrentScans", 32);
                if (activeScans > maxConcurrent)
                {
                    Response.StatusCode = 503;
                    Response.AddHeader("Retry-After", "2");
                    return JsonResponseBuilder.SystemBusy(2);
                }

                if (image == null || image.ContentLength <= 0)
                {
                    return JsonResponseBuilder.Error("NO_IMAGE");
                }

                // Location validation for mobile
                if (DeviceService.IsMobileDevice(Request) && (!lat.HasValue || !lon.HasValue))
                {
                    return JsonResponseBuilder.Error("GPS_REQUIRED", "Location required for mobile attendance");
                }

                string tempPath = null;
                string processedPath = null;

                try
                {
                    var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
                    tempPath = FileSecurityService.SaveTemp(image, "att_", maxBytes);

                    bool isProcessed;
                    processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "att_", out isProcessed);

                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    FaceRecognitionDotNet.Location faceLoc;
                    string detectErr;

                    // Use client face box if provided
                    if (faceX.HasValue && faceY.HasValue && faceW.HasValue && faceH.HasValue
                        && faceW.Value > 20 && faceH.Value > 20)
                    {
                        faceBox = new DlibBiometrics.FaceBox
                        {
                            Left = faceX.Value,
                            Top = faceY.Value,
                            Width = faceW.Value,
                            Height = faceH.Value
                        };
                        faceLoc = new FaceRecognitionDotNet.Location(
                            faceBox.Left, faceBox.Top,
                            faceBox.Left + faceBox.Width,
                            faceBox.Top + faceBox.Height);
                    }
                    else if (!dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                    {
                        return JsonResponseBuilder.Error("NO_FACE", detectErr);
                    }

                    // Liveness check
                    var liveness = new OnnxLiveness();
                    var scored = liveness.ScoreFromFile(processedPath, faceBox);
                    var livenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);

                    if (!scored.Ok || (scored.Probability ?? 0) < livenessThreshold)
                    {
                        return JsonResponseBuilder.Error("LIVENESS_FAIL", "Please ensure you are a real person");
                    }

                    // Encode
                    double[] vec;
                    string encErr;
                    if (!dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr) || vec == null)
                    {
                        return JsonResponseBuilder.Error("ENCODING_FAIL");
                    }

                    // Match
                    if (!FastFaceMatcher.IsInitialized)
                        FastFaceMatcher.Initialize();

                    var matchResult = FastFaceMatcher.FindBestMatch(vec, tolerance: 0.60);

                    if (!matchResult.IsMatch)
                    {
                        return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Face not recognized");
                    }

                    // Get employee and validate
                    using (var db = new FaceAttendDBEntities())
                    {
                        var employee = db.Employees
                            .FirstOrDefault(e => e.EmployeeId == matchResult.Employee.EmployeeId);

                        if (employee == null)
                        {
                            return JsonResponseBuilder.NotFound("Employee");
                        }

                        // Validate status
                        var status = employee.Status ?? (employee.IsActive ? "ACTIVE" : "INACTIVE");
                        if (!string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                        {
                            return JsonResponseBuilder.Error("INVALID_STATUS", "Employee not active");
                        }

                        // Location validation if provided
                        if (lat.HasValue && lon.HasValue)
                        {
                            var officeValid = ValidateOfficeLocation(db, employee.OfficeId, 
                                lat.Value, lon.Value, accuracy);
                            if (!officeValid)
                            {
                                return JsonResponseBuilder.Error("INVALID_LOCATION", 
                                    "Location does not match assigned office");
                            }
                        }

                        // Record attendance
                        var attendanceService = new AttendanceService(db);
                        var recordResult = attendanceService.RecordAttendance(
                            employee.Id, 
                            requestedAtUtc, 
                            lat, lon, accuracy,
                            Request.UserHostAddress);

                        return JsonResponseBuilder.Success(new
                        {
                            type = recordResult.Type,
                            employee = new
                            {
                                id = employee.EmployeeId,
                                name = employee.FirstName + " " + employee.LastName,
                                department = employee.Department,
                                position = employee.Position
                            },
                            timestamp = requestedAtUtc,
                            confidence = matchResult.Confidence,
                            message = recordResult.Message
                        });
                    }
                }
                finally
                {
                    ImagePreprocessor.Cleanup(processedPath, tempPath);
                    FileSecurityService.TryDelete(tempPath);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeScans);
            }
        }

        /// <summary>
        /// Record attendance with burst mode (multi-frame consensus)
        /// </summary>
        [HttpPost]
        [Route("burst")]
        [ValidateAntiForgeryToken]
        public ActionResult Burst(double? lat = null, double? lon = null, double? accuracy = null)
        {
            var tempFiles = new List<string>();
            try
            {
                int frameCount = 0;
                int.TryParse(Request.Form["frameCount"], out frameCount);
                if (frameCount < 1) frameCount = 5;

                // Collect frames
                var framePaths = new List<Tuple<int, string>>();
                for (int i = 0; i < frameCount; i++)
                {
                    var image = Request.Files["frame_" + i] ?? Request.Files["image_" + i];
                    if (image == null || image.ContentLength == 0) continue;

                    var tempPath = Path.Combine(Path.GetTempPath(), $"burst_{Guid.NewGuid():N}.jpg");
                    tempFiles.Add(tempPath);
                    image.SaveAs(tempPath);
                    framePaths.Add(Tuple.Create(i, tempPath));
                }

                if (framePaths.Count < 1)
                {
                    return JsonResponseBuilder.Error("NO_FRAMES");
                }

                // Process in parallel
                var results = new ConcurrentBag<FrameMatchResult>();
                double livenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);

                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();

                Parallel.ForEach(framePaths, new ParallelOptions { MaxDegreeOfParallelism = 3 }, 
                    (frameInfo) =>
                {
                    var tempPath = frameInfo.Item2;
                    try
                    {
                        var dlib = new DlibBiometrics();
                        var liveness = new OnnxLiveness();

                        DlibBiometrics.FaceBox faceBox;
                        FaceRecognitionDotNet.Location faceLoc;
                        string detectErr;

                        if (!dlib.TryDetectSingleFaceFromFile(tempPath, out faceBox, out faceLoc, out detectErr))
                            return;

                        var scored = liveness.ScoreFromFile(tempPath, faceBox);
                        if (!scored.Ok || (scored.Probability ?? 0) < livenessThreshold)
                            return;

                        double[] vec;
                        string encErr;
                        if (!dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out vec, out encErr) || vec == null)
                            return;

                        var matchResult = FastFaceMatcher.FindBestMatch(vec, tolerance: 0.60);

                        results.Add(new FrameMatchResult
                        {
                            EmployeeId = matchResult.IsMatch ? matchResult.Employee?.EmployeeId : null,
                            Confidence = matchResult.Confidence,
                            IsMatch = matchResult.IsMatch
                        });
                    }
                    catch { }
                });

                var frameResults = results.ToList();

                // Consensus voting
                int minRequired = Math.Min(2, frameResults.Count);
                if (frameResults.Count < minRequired)
                {
                    return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Could not detect clear face");
                }

                var successfulMatches = frameResults.Where(r => r.IsMatch && !string.IsNullOrEmpty(r.EmployeeId)).ToList();
                int minMatchesRequired = frameResults.Count >= 3 ? 2 : 1;
                if (successfulMatches.Count < minMatchesRequired)
                {
                    return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Face not recognized");
                }

                // Find winner
                var voteGroups = successfulMatches
                    .GroupBy(r => r.EmployeeId)
                    .Select(g => new { EmployeeId = g.Key, Votes = g.Count(), AvgConfidence = g.Average(r => r.Confidence) })
                    .OrderByDescending(g => g.Votes)
                    .ThenByDescending(g => g.AvgConfidence)
                    .ToList();

                var winner = voteGroups.First();
                int requiredVotes = frameResults.Count >= 3 ? 2 : 1;
                if (winner.Votes < requiredVotes)
                {
                    return JsonResponseBuilder.Error("NOT_RECOGNIZED", "Insufficient consensus");
                }

                // Record attendance for winner
                using (var db = new FaceAttendDBEntities())
                {
                    var employee = db.Employees.FirstOrDefault(e => e.EmployeeId == winner.EmployeeId);
                    if (employee == null)
                    {
                        return JsonResponseBuilder.NotFound("Employee");
                    }

                    var status = employee.Status ?? (employee.IsActive ? "ACTIVE" : "INACTIVE");
                    if (!string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResponseBuilder.Error("INVALID_STATUS");
                    }

                    var attendanceService = new AttendanceService(db);
                    var recordResult = attendanceService.RecordAttendance(
                        employee.Id,
                        DateTime.UtcNow,
                        lat, lon, accuracy,
                        Request.UserHostAddress);

                    return JsonResponseBuilder.Success(new
                    {
                        type = recordResult.Type,
                        employee = new
                        {
                            id = employee.EmployeeId,
                            name = employee.FirstName + " " + employee.LastName
                        },
                        consensusVotes = winner.Votes,
                        totalFrames = frameResults.Count,
                        confidence = winner.AvgConfidence,
                        message = recordResult.Message
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[AttendanceController.Burst] Error: {0}", ex);
                return JsonResponseBuilder.Error("SERVER_ERROR");
            }
            finally
            {
                foreach (var file in tempFiles)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        private bool ValidateOfficeLocation(FaceAttendDBEntities db, int? officeId, 
            double lat, double lon, double? accuracy)
        {
            if (!officeId.HasValue) return true;

            var office = db.Offices.Find(officeId.Value);
            if (office == null || !office.Latitude.HasValue || !office.Longitude.HasValue)
                return true;

            var distance = OfficeLocationService.CalculateDistance(
                lat, lon, 
                office.Latitude.Value, office.Longitude.Value);

            var requiredAccuracy = ConfigurationService.GetInt("Location:GPSAccuracyRequired", 50);
            var radius = office.RadiusMeters ?? ConfigurationService.GetInt("Location:GPSRadiusDefault", 100);

            return distance <= radius + (accuracy ?? 0) + requiredAccuracy;
        }

        private class FrameMatchResult
        {
            public string EmployeeId { get; set; }
            public double Confidence { get; set; }
            public bool IsMatch { get; set; }
        }
    }
}
