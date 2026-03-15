using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FaceAttend.Services.Security;
using FaceRecognitionDotNet;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Unified face scanning service
    /// Consolidates scanning logic from all controllers
    /// </summary>
    public interface IFaceScanner
    {
        /// <summary>
        /// Scan a single frame for face detection
        /// </summary>
        ScanResult Scan(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false);

        /// <summary>
        /// Scan with async preprocessing
        /// </summary>
        Task<ScanResult> ScanAsync(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false);

        /// <summary>
        /// Enroll multiple frames for an employee
        /// </summary>
        EnrollmentResult Enroll(string employeeId, IEnumerable<Stream> imageStreams, bool isMobile = false);

        /// <summary>
        /// Match face against database
        /// </summary>
        MatchResult Match(Stream imageStream, double tolerance = 0.6);

        /// <summary>
        /// Validate face without matching (quick check)
        /// </summary>
        ValidationResult Validate(Stream imageStream);
    }

    /// <summary>
    /// Implementation of unified face scanning
    /// </summary>
    public class FaceScanner : IFaceScanner
    {
        private readonly DlibBiometrics _dlib;
        private readonly OnnxLiveness _liveness;

        public FaceScanner()
        {
            _dlib = new DlibBiometrics();
            _liveness = new OnnxLiveness();
        }

        /// <inheritdoc />
        public ScanResult Scan(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false)
        {
            if (imageStream == null || imageStream.Length == 0)
            {
                return ScanResult.Fail("NO_IMAGE", "No image provided");
            }

            string tempPath = null;
            string processedPath = null;

            try
            {
                // Save to temp
                tempPath = SaveStreamToTemp(imageStream, "scan_");

                // Preprocess
                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "scan_", out isProcessed);

                // Detect face
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (clientBox != null)
                {
                    faceBox = clientBox;
                    faceLoc = new FaceRecognitionDotNet.Location(
                        clientBox.Left, clientBox.Top,
                        clientBox.Left + clientBox.Width,
                        clientBox.Top + clientBox.Height);
                }
                else if (!_dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                {
                    return ScanResult.Fail("NO_FACE", detectErr ?? "No face detected");
                }

                // Calculate sharpness
                var sharpness = FaceQualityAnalyzer.CalculateSharpness(processedPath, faceBox);
                var sharpnessThreshold = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);

                // Liveness check
                var livenessResult = _liveness.ScoreFromFile(processedPath, faceBox);
                var livenessThreshold = (float)ConfigurationService.GetDouble(
                    "Biometrics:LivenessThreshold", 0.75);

                // Encode face
                double[] encoding;
                string encodeErr;
                string base64Encoding = null;

                if (_dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out encoding, out encodeErr)
                    && encoding != null)
                {
                    base64Encoding = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(encoding));
                }

                // Get actual image dimensions for accurate pose estimation
                int imgWidth = 640, imgHeight = 480;
                try
                {
                    if (!string.IsNullOrEmpty(processedPath) && System.IO.File.Exists(processedPath))
                        using (var img = System.Drawing.Image.FromFile(processedPath))
                        {
                            imgWidth = img.Width;
                            imgHeight = img.Height;
                        }
                }
                catch { /* Use fallback */ }

                // Estimate pose
                var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgWidth, imgHeight);
                var poseBucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                return new ScanResult
                {
                    Ok = true,
                    Count = 1,
                    Liveness = livenessResult.Probability ?? 0,
                    LivenessOk = livenessResult.Ok && (livenessResult.Probability ?? 0) >= livenessThreshold,
                    LivenessThreshold = livenessThreshold,
                    Sharpness = sharpness,
                    SharpnessThreshold = sharpnessThreshold,
                    SharpnessOk = sharpness >= sharpnessThreshold,
                    Encoding = base64Encoding,
                    PoseYaw = yaw,
                    PosePitch = pitch,
                    PoseBucket = poseBucket,
                    FaceBox = faceBox
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

        /// <inheritdoc />
        public Task<ScanResult> ScanAsync(Stream imageStream, DlibBiometrics.FaceBox clientBox = null, bool isMobile = false)
        {
            return Task.Run(() => Scan(imageStream, clientBox, isMobile));
        }

        /// <inheritdoc />
        public EnrollmentResult Enroll(string employeeId, IEnumerable<Stream> imageStreams, bool isMobile = false)
        {
            if (string.IsNullOrWhiteSpace(employeeId))
            {
                return EnrollmentResult.Fail("NO_EMPLOYEE_ID");
            }

            var streams = imageStreams?.ToList();
            if (streams == null || streams.Count == 0)
            {
                return EnrollmentResult.Fail("NO_IMAGE");
            }

            // Configuration
            var maxImages = ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget", 8);
            var maxStored = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 5);
            var strictTol = ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45);

            streams = streams.Take(maxImages).ToList();

            // Process frames in parallel
            var candidates = new ConcurrentBag<EnrollCandidate>();
            int processedCount = 0;
            bool duplicateFound = false;
            string duplicateId = null;
            var lockObj = new object();

            var parallelism = Math.Min(streams.Count,
                ConfigurationService.GetInt("Biometrics:Enroll:Parallelism", 4));

            Parallel.ForEach(streams, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                (stream, state) =>
            {
                if (duplicateFound) return;

                ProcessEnrollFrame(
                    stream, isMobile, strictTol, lockObj,
                    candidates, ref processedCount, ref duplicateFound, ref duplicateId,
                    state);
            });

            if (duplicateFound)
            {
                return EnrollmentResult.Fail("FACE_ALREADY_ENROLLED",
                    $"Face already enrolled: {duplicateId}");
            }

            if (candidates.IsEmpty)
            {
                return EnrollmentResult.Fail("NO_GOOD_FRAME",
                    $"No good frames found. Processed: {processedCount}");
            }

            // Diversity-aware selection
            var selected = SelectDiverseFrames(candidates.ToList(), maxStored);

            return new EnrollmentResult
            {
                Ok = true,
                SavedVectors = selected.Count,
                Candidates = selected,
                PoseBuckets = selected.Select(c => c.PoseBucket).ToList()
            };
        }

        /// <inheritdoc />
        public MatchResult Match(Stream imageStream, double tolerance = 0.6)
        {
            if (imageStream == null || imageStream.Length == 0)
            {
                return MatchResult.Fail("No image provided");
            }

            string tempPath = null;
            string processedPath = null;

            try
            {
                tempPath = SaveStreamToTemp(imageStream, "match_");

                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "match_", out isProcessed);

                // Detect face
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!_dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                {
                    return MatchResult.Fail("No face detected");
                }

                // Encode
                double[] vec;
                string encErr;
                if (!_dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out vec, out encErr) || vec == null)
                {
                    return MatchResult.Fail("Could not encode face");
                }

                // Match
                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();

                var result = FastFaceMatcher.FindBestMatch(vec, tolerance);

                return new MatchResult
                {
                    Ok = true,
                    IsMatch = result.IsMatch,
                    Employee = result.Employee,
                    Confidence = result.Confidence,
                    Distance = result.Distance
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

        /// <inheritdoc />
        public ValidationResult Validate(Stream imageStream)
        {
            if (imageStream == null || imageStream.Length == 0)
            {
                return ValidationResult.Fail("No image provided");
            }

            string tempPath = null;

            try
            {
                tempPath = SaveStreamToTemp(imageStream, "val_");

                // Detect face
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!_dlib.TryDetectSingleFaceFromFile(tempPath, out faceBox, out faceLoc, out detectErr))
                {
                    return new ValidationResult
                    {
                        Ok = true,
                        IsValid = false,
                        FaceDetected = false,
                        Message = "No face detected"
                    };
                }

                // Try to encode
                double[] vec;
                string encErr;
                if (!_dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out vec, out encErr) || vec == null)
                {
                    return new ValidationResult
                    {
                        Ok = true,
                        IsValid = false,
                        FaceDetected = true,
                        Encodable = false,
                        Message = "Could not encode face"
                    };
                }

                return new ValidationResult
                {
                    Ok = true,
                    IsValid = true,
                    FaceDetected = true,
                    Encodable = true
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[FaceScanner.Validate] Error: {0}", ex);
                return ValidationResult.Fail("Validation error");
            }
            finally
            {
                if (tempPath != null)
                    FileSecurityService.TryDelete(tempPath);
            }
        }

        #region Helper Methods

        private void ProcessEnrollFrame(
            Stream stream, bool isMobile, double strictTol, object lockObj,
            ConcurrentBag<EnrollCandidate> candidates, ref int processedCount,
            ref bool duplicateFound, ref string duplicateId,
            ParallelLoopState state)
        {
            string path = null, processedPath = null;
            try
            {
                var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
                path = SaveStreamToTemp(stream, "enr_");

                bool isProc;
                processedPath = ImagePreprocessor.PreprocessForDetection(path, "enr_", out isProc);

                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectErr;

                if (!_dlib.TryDetectSingleFaceFromFile(processedPath, out faceBox, out faceLoc, out detectErr))
                    return;

                // Sharpness check
                var sharpness = FaceQualityAnalyzer.CalculateSharpness(processedPath, faceBox);
                var sharpTh = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
                if (sharpness < sharpTh) return;

                // Parallel: liveness + encoding
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
                        liveOk = scored.Ok;
                        liveness = scored.Probability ?? 0f;
                    });

                var liveTh = (float)ConfigurationService.GetDouble(
                    "Biometrics:Enroll:LivenessThreshold", 0.75);

                if (!liveOk || liveness < liveTh || vec == null) return;

                // Duplicate check
                lock (lockObj)
                {
                    processedCount++;
                    if (!duplicateFound)
                    {
                        using (var checkDb = new FaceAttendDBEntities())
                        {
                            var dup = DuplicateCheckHelper.FindDuplicate(
                                checkDb, vec, null, strictTol);

                            if (!string.IsNullOrEmpty(dup))
                            {
                                duplicateFound = true;
                                duplicateId = dup;
                                state.Stop();
                                return;
                            }
                        }
                    }
                }

                if (duplicateFound) return;

                // Pose estimation
                int imgW = 640, imgH = 480;
                try
                {
                    using (var bmp = new System.Drawing.Bitmap(processedPath))
                    {
                        imgW = bmp.Width;
                        imgH = bmp.Height;
                    }
                }
                catch { }

                var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(faceBox, imgW, imgH);
                var bucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                if (bucket == "other") return;

                int area = Math.Max(0, faceBox.Width) * Math.Max(0, faceBox.Height);

                candidates.Add(new EnrollCandidate
                {
                    Vec = vec,
                    Liveness = liveness,
                    Area = area,
                    Sharpness = sharpness,
                    PoseYaw = yaw,
                    PosePitch = pitch,
                    PoseBucket = bucket,
                    QualityScore = FaceQualityAnalyzer.CalculateQualityScore(
                        liveness, sharpness, area, yaw, pitch)
                });
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                FileSecurityService.TryDelete(path);
            }
        }

        private static List<EnrollCandidate> SelectDiverseFrames(
            List<EnrollCandidate> candidates, int targetCount)
        {
            var desiredBuckets = new[] { "center", "left", "right", "up", "down" };
            var selected = new List<EnrollCandidate>();

            // Phase 1: Best from each bucket
            foreach (var bucket in desiredBuckets)
            {
                if (selected.Count >= targetCount) break;

                var best = candidates
                    .Where(c => c.PoseBucket == bucket && !selected.Contains(c))
                    .OrderByDescending(c => c.QualityScore)
                    .FirstOrDefault();

                if (best != null) selected.Add(best);
            }

            // Phase 2: Fill with highest quality
            var remaining = candidates
                .Where(c => !selected.Contains(c))
                .OrderByDescending(c => c.QualityScore)
                .Take(targetCount - selected.Count);

            selected.AddRange(remaining);

            return selected
                .OrderByDescending(c => c.QualityScore)
                .ToList();
        }

        private string SaveStreamToTemp(Stream stream, string prefix)
        {
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N") + ".jpg");

            using (var fileStream = File.Create(tempPath))
            {
                stream.Position = 0;
                stream.CopyTo(fileStream);
            }

            return tempPath;
        }

        private void Cleanup(string tempPath, string processedPath)
        {
            ImagePreprocessor.Cleanup(processedPath, tempPath);
            if (tempPath != null)
                FileSecurityService.TryDelete(tempPath);
        }

        #endregion
    }

    #region Result Classes

    public class ScanResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Message { get; set; }
        public int Count { get; set; }
        public float Liveness { get; set; }
        public bool LivenessOk { get; set; }
        public float LivenessThreshold { get; set; }
        public float Sharpness { get; set; }
        public float SharpnessThreshold { get; set; }
        public bool SharpnessOk { get; set; }
        public string Encoding { get; set; }
        public float PoseYaw { get; set; }
        public float PosePitch { get; set; }
        public string PoseBucket { get; set; }
        public DlibBiometrics.FaceBox FaceBox { get; set; }

        public static ScanResult Fail(string error, string message = null)
        {
            return new ScanResult { Ok = false, Error = error, Message = message };
        }
    }

    public class EnrollmentResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Message { get; set; }
        public int SavedVectors { get; set; }
        public List<EnrollCandidate> Candidates { get; set; }
        public List<string> PoseBuckets { get; set; }

        public static EnrollmentResult Fail(string error, string message = null)
        {
            return new EnrollmentResult { Ok = false, Error = error, Message = message };
        }
    }

    public class MatchResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool IsMatch { get; set; }
        public FastFaceMatcher.EmployeeInfo Employee { get; set; }
        public double Confidence { get; set; }
        public double Distance { get; set; }

        public static MatchResult Fail(string error)
        {
            return new MatchResult { Ok = false, Error = error };
        }
    }

    public class ValidationResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public bool IsValid { get; set; }
        public bool FaceDetected { get; set; }
        public bool Encodable { get; set; }
        public string Message { get; set; }

        public static ValidationResult Fail(string error)
        {
            return new ValidationResult { Ok = false, Error = error };
        }
    }

    #endregion
}
