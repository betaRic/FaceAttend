using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FaceAttend.Services.Security;
using FaceRecognitionDotNet;

namespace FaceAttend.Services.Biometrics
{
    public interface IFaceScanner
    {
        ScanResult Scan(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false);
        Task<ScanResult> ScanAsync(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false);
        EnrollmentResult Enroll(string employeeId, IEnumerable<Stream> imageStreams, bool isMobile = false);
        MatchResult Match(Stream imageStream, double tolerance = 0.6);
        ValidationResult Validate(Stream imageStream);
    }

    public class FaceScanner : IFaceScanner
    {
        private readonly DlibBiometrics _dlib;
        private readonly OnnxLiveness _liveness;

        public FaceScanner()
        {
            _dlib     = new DlibBiometrics();
            _liveness = new OnnxLiveness();
        }

        public ScanResult Scan(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false)
        {
            if (imageStream == null || imageStream.Length == 0)
                return ScanResult.Fail("NO_IMAGE", "No image provided");

            string tempPath = null, processedPath = null;
            try
            {
                tempPath = SaveStreamToTemp(imageStream, "scan_");

                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "scan_", out isProcessed);

                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (clientBox != null)
                {
                    faceBox = clientBox;
                    faceLoc = new FaceRecognitionDotNet.Location(
                        clientBox.Left, clientBox.Top,
                        clientBox.Left + clientBox.Width,
                        clientBox.Top  + clientBox.Height);
                }
                else if (!_dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                {
                    return ScanResult.Fail("NO_FACE", detectErr ?? "No face detected");
                }

                float sharpness, sharpnessThreshold;
                using (var bmp = new Bitmap(processedPath))
                {
                    sharpness          = FaceQualityAnalyzer.CalculateSharpnessFromBitmap(bmp, faceBox);
                    sharpnessThreshold = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
                }

                var livenessResult    = _liveness.ScoreFromFile(processedPath, faceBox);
                var livenessThreshold = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);

                double[] encoding; string encodeErr;
                string base64Encoding = null;
                if (_dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out encoding, out encodeErr) && encoding != null)
                    base64Encoding = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(encoding));

                int imgWidth = 640, imgHeight = 480;
                try
                {
                    if (!string.IsNullOrEmpty(processedPath) && File.Exists(processedPath))
                        using (var img = System.Drawing.Image.FromFile(processedPath))
                        { imgWidth = img.Width; imgHeight = img.Height; }
                }
                catch { }

                var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgWidth, imgHeight);
                var poseBucket   = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                return new ScanResult
                {
                    Ok                 = true,
                    Count              = 1,
                    Liveness           = livenessResult.Probability ?? 0,
                    LivenessOk         = livenessResult.Ok && (livenessResult.Probability ?? 0) >= livenessThreshold,
                    LivenessThreshold  = livenessThreshold,
                    Sharpness          = sharpness,
                    SharpnessThreshold = sharpnessThreshold,
                    SharpnessOk        = sharpness >= sharpnessThreshold,
                    Encoding           = base64Encoding,
                    PoseYaw            = yaw,
                    PosePitch          = pitch,
                    PoseBucket         = poseBucket,
                    FaceBox            = faceBox
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[FaceScanner.Scan] Error: {0}", ex);
                return ScanResult.Fail("SCAN_ERROR", "Face scanning failed");
            }
            finally
            {
                Cleanup(tempPath, processedPath);
            }
        }

        public Task<ScanResult> ScanAsync(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false)
        {
            return Task.Run(() => Scan(imageStream, clientBox, isMobile));
        }

        public EnrollmentResult Enroll(string employeeId, IEnumerable<Stream> imageStreams, bool isMobile = false)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
                return EnrollmentResult.Fail("NO_EMPLOYEE_ID");

            var streams = imageStreams?.ToList();
            if (streams == null || streams.Count == 0)
                return EnrollmentResult.Fail("NO_IMAGE");

            var maxImages   = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget",    8);
            var maxStored   = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
            var strictTol   = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);
            var parallelism = Math.Min(streams.Count,
                              ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));

            streams = streams.Take(maxImages).ToList();

            var candidates     = new ConcurrentBag<EnrollCandidate>();
            int processedCount = 0;

            Parallel.ForEach(streams, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, stream =>
            {
                ProcessEnrollFrame(stream, isMobile, candidates, ref processedCount);
            });

            if (candidates.IsEmpty)
                return EnrollmentResult.Fail("NO_GOOD_FRAME", $"No good frames found. Processed: {processedCount}");

            var selected = SelectDiverseByEmbedding(candidates.ToList(), maxStored);

            string duplicateId = null;
            using (var checkDb = new FaceAttendDBEntities())
                duplicateId = DuplicateCheckHelper.FindDuplicate(checkDb, selected[0].Vec, employeeId, strictTol);

            if (!string.IsNullOrEmpty(duplicateId))
                return EnrollmentResult.Fail("FACE_ALREADY_ENROLLED", $"Face already enrolled: {duplicateId}");

            return new EnrollmentResult
            {
                Ok           = true,
                SavedVectors = selected.Count,
                Candidates   = selected
            };
        }

        public MatchResult Match(Stream imageStream, double tolerance = 0.6)
        {
            if (imageStream == null || imageStream.Length == 0)
                return MatchResult.Fail("No image provided");

            string tempPath = null, processedPath = null;
            try
            {
                tempPath = SaveStreamToTemp(imageStream, "match_");

                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "match_", out isProcessed);

                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!_dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                    return MatchResult.Fail("No face detected");

                double[] vec; string encErr;
                if (!_dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr) || vec == null)
                    return MatchResult.Fail("Could not encode face");

                if (!FastFaceMatcher.IsInitialized) FastFaceMatcher.Initialize();

                var result = FastFaceMatcher.FindBestMatch(vec, tolerance);

                return new MatchResult
                {
                    Ok         = true,
                    IsMatch    = result.IsMatch,
                    Employee   = result.Employee,
                    Confidence = result.Confidence,
                    Distance   = result.Distance
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[FaceScanner.Match] Error: {0}", ex);
                return MatchResult.Fail("Matching failed");
            }
            finally
            {
                Cleanup(tempPath, processedPath);
            }
        }

        public ValidationResult Validate(Stream imageStream)
        {
            if (imageStream == null || imageStream.Length == 0)
                return ValidationResult.Fail("No image provided");

            string tempPath = null;
            try
            {
                tempPath = SaveStreamToTemp(imageStream, "val_");

                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!_dlib.TryDetectSingleFaceFromFile(tempPath, out faceBox, out faceLoc, out detectErr))
                    return new ValidationResult { Ok = true, IsValid = false, FaceDetected = false, Message = "No face detected" };

                double[] vec; string encErr;
                if (!_dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out vec, out encErr) || vec == null)
                    return new ValidationResult { Ok = true, IsValid = false, FaceDetected = true, Encodable = false, Message = "Could not encode face" };

                return new ValidationResult { Ok = true, IsValid = true, FaceDetected = true, Encodable = true };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[FaceScanner.Validate] Error: {0}", ex);
                return ValidationResult.Fail("Validation error");
            }
            finally
            {
                if (tempPath != null) FileSecurityService.TryDelete(tempPath);
            }
        }

        private void ProcessEnrollFrame(
            Stream stream, bool isMobile,
            ConcurrentBag<EnrollCandidate> candidates, ref int processedCount)
        {
            string path = null, processedPath = null;
            try
            {
                path = SaveStreamToTemp(stream, "enr_");

                bool isProc;
                processedPath = ImagePreprocessor.PreprocessForDetection(path, "enr_", out isProc);

                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!_dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                    return;

                float sharpness;
                using (var bmp = new Bitmap(processedPath))
                    sharpness = FaceQualityAnalyzer.CalculateSharpnessFromBitmap(bmp, faceBox);

                var sharpTh = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
                if (sharpness < sharpTh) return;

                double[] vec = null;
                float liveness = 0f;
                bool liveOk = false;

                Parallel.Invoke(
                    () =>
                    {
                        string encErr;
                        _dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr);
                    },
                    () =>
                    {
                        var scored = _liveness.ScoreFromFile(processedPath, faceBox);
                        liveOk    = scored.Ok;
                        liveness  = scored.Probability ?? 0f;
                    });

                var liveTh = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
                if (!liveOk || liveness < liveTh || vec == null) return;

                System.Threading.Interlocked.Increment(ref processedCount);

                int imgW = 640, imgH = 480;
                try
                {
                    using (var bmp = new Bitmap(processedPath))
                    { imgW = bmp.Width; imgH = bmp.Height; }
                }
                catch { }

                var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgW, imgH);
                if (Math.Abs(yaw) > 45f || Math.Abs(pitch) > 55f) return;

                int area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);

                candidates.Add(new EnrollCandidate
                {
                    Vec          = vec,
                    Liveness     = liveness,
                    Area         = area,
                    Sharpness    = sharpness,
                    PoseYaw      = yaw,
                    PosePitch    = pitch,
                    QualityScore = FaceQualityAnalyzer.CalculateQualityScore(liveness, sharpness, area, yaw, pitch)
                });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                FileSecurityService.TryDelete(path);
            }
        }

        private static List<EnrollCandidate> SelectDiverseByEmbedding(
            List<EnrollCandidate> candidates, int targetCount)
        {
            if (candidates.Count <= targetCount)
                return candidates.OrderByDescending(c => c.QualityScore).ToList();

            var sorted    = candidates.OrderByDescending(c => c.QualityScore).ToList();
            var selected  = new List<EnrollCandidate>(targetCount) { sorted[0] };
            var remaining = sorted.Skip(1).ToList();

            while (selected.Count < targetCount && remaining.Count > 0)
            {
                double bestMinDist = -1;
                int    bestIdx     = 0;

                for (int i = 0; i < remaining.Count; i++)
                {
                    double minDist = double.MaxValue;
                    foreach (var s in selected)
                    {
                        var d = DlibBiometrics.Distance(remaining[i].Vec, s.Vec);
                        if (d < minDist) minDist = d;
                    }
                    if (minDist > bestMinDist) { bestMinDist = minDist; bestIdx = i; }
                }

                selected.Add(remaining[bestIdx]);
                remaining.RemoveAt(bestIdx);
            }

            return selected.OrderByDescending(c => c.QualityScore).ToList();
        }

        private string SaveStreamToTemp(Stream stream, string prefix)
        {
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N") + ".jpg");

            using (var fs = File.Create(tempPath))
            {
                stream.Position = 0;
                stream.CopyTo(fs);
            }

            return tempPath;
        }

        private void Cleanup(string tempPath, string processedPath)
        {
            ImagePreprocessor.Cleanup(processedPath, tempPath);
            if (tempPath != null) FileSecurityService.TryDelete(tempPath);
        }
    }

    public class ScanResult
    {
        public bool   Ok                 { get; set; }
        public string Error              { get; set; }
        public string Message            { get; set; }
        public int    Count              { get; set; }
        public float  Liveness           { get; set; }
        public bool   LivenessOk         { get; set; }
        public float  LivenessThreshold  { get; set; }
        public float  Sharpness          { get; set; }
        public float  SharpnessThreshold { get; set; }
        public bool   SharpnessOk        { get; set; }
        public string Encoding           { get; set; }
        public float  PoseYaw            { get; set; }
        public float  PosePitch          { get; set; }
        public string PoseBucket         { get; set; }
        public DlibBiometrics.FaceBox FaceBox { get; set; }

        public static ScanResult Fail(string error, string message = null) =>
            new ScanResult { Ok = false, Error = error, Message = message };
    }

    public class EnrollmentResult
    {
        public bool   Ok           { get; set; }
        public string Error        { get; set; }
        public string Message      { get; set; }
        public int    SavedVectors { get; set; }
        public List<EnrollCandidate> Candidates { get; set; }

        public static EnrollmentResult Fail(string error, string message = null) =>
            new EnrollmentResult { Ok = false, Error = error, Message = message };
    }

    public class MatchResult
    {
        public bool   Ok         { get; set; }
        public string Error      { get; set; }
        public bool   IsMatch    { get; set; }
        public FastFaceMatcher.EmployeeInfo Employee { get; set; }
        public double Confidence { get; set; }
        public double Distance   { get; set; }

        public static MatchResult Fail(string error) =>
            new MatchResult { Ok = false, Error = error };
    }

    public class ValidationResult
    {
        public bool   Ok           { get; set; }
        public string Error        { get; set; }
        public bool   IsValid      { get; set; }
        public bool   FaceDetected { get; set; }
        public bool   Encodable    { get; set; }
        public string Message      { get; set; }

        public static ValidationResult Fail(string error) =>
            new ValidationResult { Ok = false, Error = error };
    }
}
