using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using FaceRecognitionDotNet;
using FaceAttend.Services;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// OPTIMIZED: Fast scan pipeline for walk-by kiosk.
    /// 
    /// PERFORMANCE IMPROVEMENTS:
    /// 1. Parallel liveness + encoding - run simultaneously (~100-150ms saved)
    /// 2. Uses DlibBiometrics pool for thread-safe face recognition
    /// 
    /// Note: Still uses temp files because DlibBiometrics pool works with file paths.
    /// The temp file is immediately deleted after processing.
    /// </summary>
    public static class FastScanPipeline
    {
        /// <summary>
        /// Fast scan result with timing information
        /// </summary>
        public class ScanResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public double[] FaceEncoding { get; set; }
            public float LivenessScore { get; set; }
            public bool LivenessOk { get; set; }
            public DlibBiometrics.FaceBox FaceBox { get; set; }
            public long TimingMs { get; set; }
            public Dictionary<string, long> Timings { get; set; }
        }

        /// <summary>
        /// OPTIMIZED: Fast scan with parallel liveness + encoding
        /// 
        /// Flow:
        /// 1. Save image to temp file
        /// 2. Detect face using DlibBiometrics pool
        /// 3. Run liveness + encoding IN PARALLEL
        /// 4. Delete temp file
        /// 5. Return result
        /// </summary>
        public static ScanResult ScanInMemory(HttpPostedFileBase image, bool includeTimings = false)
        {
            var sw = Stopwatch.StartNew();
            var timings = includeTimings ? new Dictionary<string, long>() : null;

            string tempPath = null;

            try
            {
                // STEP 1: Save to temp file (required for DlibBiometrics pool)
                tempPath = Path.Combine(Path.GetTempPath(), $"fastscan_{Guid.NewGuid():N}.jpg");
                image.SaveAs(tempPath);
                RecordTiming(timings, "save_temp", sw);

                // STEP 2: Detect face using DlibBiometrics (uses pool)
                var dlib = new DlibBiometrics();
                DlibBiometrics.FaceBox faceBox;
                Location faceLocation;
                string detectErr;
                
                if (!dlib.TryDetectSingleFaceFromFile(tempPath, out faceBox, out faceLocation, out detectErr))
                {
                    return new ScanResult 
                    { 
                        Ok = false, 
                        Error = detectErr ?? "NO_FACE",
                        Timings = timings,
                        TimingMs = sw.ElapsedMilliseconds
                    };
                }
                RecordTiming(timings, "detect", sw);

                // STEP 3: PARALLEL - Liveness + Encoding
                double[] encoding = null;
                bool livenessOk = false;
                float? livenessProb = null;

                Parallel.Invoke(
                    () =>
                    {
                        // Face encoding using DlibBiometrics (uses pool)
                        string encErr;
                        dlib.TryEncodeFromFileWithLocation(tempPath, faceLocation, out encoding, out encErr);
                    },
                    () =>
                    {
                        // Liveness check
                        var live = new OnnxLiveness();
                        var result = live.ScoreFromFile(tempPath, faceBox);
                        livenessOk = result.Ok;
                        livenessProb = result.Probability;
                    }
                );
                RecordTiming(timings, "parallel_liveness_encode", sw);

                // Check results
                if (encoding == null)
                {
                    return new ScanResult 
                    { 
                        Ok = false, 
                        Error = "ENCODING_FAIL",
                        Timings = timings,
                        TimingMs = sw.ElapsedMilliseconds
                    };
                }

                var liveTh = (float)ConfigurationService.GetDouble(
                    "Biometrics:LivenessThreshold", 0.75);
                
                var livenessScore = livenessProb ?? 0f;
                var livenessPassed = livenessOk && livenessScore >= liveTh;

                return new ScanResult
                {
                    Ok = true,
                    FaceEncoding = encoding,
                    LivenessScore = livenessScore,
                    LivenessOk = livenessPassed,
                    FaceBox = faceBox,
                    Timings = timings,
                    TimingMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[FastScanPipeline] Error: " + ex);
                return new ScanResult 
                { 
                    Ok = false, 
                    Error = "SCAN_ERROR",
                    Timings = timings,
                    TimingMs = sw.ElapsedMilliseconds
                };
            }
            finally
            {
                // Cleanup temp file
                if (!string.IsNullOrEmpty(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        private static void RecordTiming(Dictionary<string, long> timings, string key, Stopwatch sw)
        {
            if (timings != null)
            {
                timings[key] = sw.ElapsedMilliseconds;
            }
        }
    }
}
