using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    /// 1. Single JPEG decode — image loaded once as Bitmap, reused for all operations (~30-45ms saved)
    /// 2. Parallel liveness + encoding — run simultaneously (~100-150ms saved)
    /// 3. Uses DlibBiometrics pool for thread-safe face recognition
    /// 
    /// PHASE 3 OPTIMIZATION (P-03): Removed temp file usage — all processing in-memory.
    /// Previous: Save to temp file → 3× file reads (detect, encode, liveness)
    /// Current:  Load Bitmap once → reuse for all operations
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
            public float Sharpness { get; set; }          // NEW: Phase 1 - sharpness score
            public int ImageWidth { get; set; }           // NEW: For angle-aware tolerance in KioskController
            public long TimingMs { get; set; }
            public Dictionary<string, long> Timings { get; set; }
        }

        /// <summary>
        /// Enrollment scan result — superset of ScanResult, adds landmarks and encoding string.
        /// </summary>
        public class EnrollmentScanResult
        {
            public bool   Ok            { get; set; }
            public string Error         { get; set; }
            public string Base64Encoding { get; set; }   // for ScanFrame JSON response
            public double[] FaceEncoding { get; set; }  // raw 128d vector
            public float[]  Landmarks5  { get; set; }   // [leX,leY,reX,reY,ntX,ntY] pixels
            public float    LivenessScore { get; set; }
            public bool     LivenessOk  { get; set; }
            public float    Sharpness   { get; set; }
            public float    SharpnessThreshold { get; set; }
            public DlibBiometrics.FaceBox FaceBox { get; set; }
            public int      ImageWidth  { get; set; }
            public int      ImageHeight { get; set; }
            public long     TimingMs    { get; set; }
        }

        /// <summary>
        /// ENROLLMENT-OPTIMISED: single Bitmap decode, then three-way parallel:
        ///   Thread A — liveness (ONNX, reuses Bitmap)
        ///   Thread B — encoding (dlib ResNet, reuses Bitmap)
        ///   Thread C — landmarks 5-point (dlib, same pool rent as encoding via combined method)
        /// Sharpness runs on the main thread before the parallel block (fast, ~5ms).
        ///
        /// Replaces the 5-file-read sequential pipeline in ScanController.Frame.
        /// Performance target: 300–600ms total (was 1–3s).
        /// </summary>
        public static EnrollmentScanResult EnrollmentScanInMemory(
            HttpPostedFileBase image,
            DlibBiometrics.FaceBox clientFaceBox = null,
            bool isMobile = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ── Step 1: Read ALL bytes into array first ─────────────────────
            // CRITICAL: Never pass a MemoryStream directly to new Bitmap() inside a
            // using block. Bitmap(Stream) for JPEG keeps a live stream reference and
            // reads lazily — disposing the stream before pixel access throws
            // ObjectDisposedException on the very first LockBits call.
            byte[] rawBytes;
            try
            {
                image.InputStream.Position = 0;
                using (var readMs = new MemoryStream())
                {
                    image.InputStream.CopyTo(readMs);
                    rawBytes = readMs.ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[EnrollmentScan] Read failed: " + ex.Message);
                return new EnrollmentScanResult { Ok = false, Error = "IMAGE_LOAD_FAIL" };
            }

            // ── Step 2: Decode to fully-independent Bitmap ──────────────────
            // Clone() with an explicit PixelFormat forces a complete pixel data copy.
            // The resulting Bitmap owns its own memory — no stream reference kept.
            // This is the standard .NET fix for the "Bitmap from disposed stream" bug.
            Bitmap bitmap;
            int imageWidth, imageHeight;
            try
            {
                using (var decodeMs = new MemoryStream(rawBytes))
                using (var rawBmp = new Bitmap(decodeMs))
                {
                    imageWidth  = rawBmp.Width;
                    imageHeight = rawBmp.Height;
                    bitmap = rawBmp.Clone(
                        new Rectangle(0, 0, rawBmp.Width, rawBmp.Height),
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                }
                // decodeMs and rawBmp disposed here — bitmap is safe, owns its pixels
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[EnrollmentScan] Bitmap decode failed: " + ex.Message);
                return new EnrollmentScanResult
                {
                    Ok = false, Error = "IMAGE_LOAD_FAIL",
                    TimingMs = sw.ElapsedMilliseconds
                };
            }

            using (bitmap)
            {
                // ── Step 3: Detect or trust client box ──────────────────────
                var dlib = new DlibBiometrics();
                DlibBiometrics.FaceBox faceBox;
                Location faceLocation;
                string detectErr;

                // reportedFaceBox = what we return to the client (stable, no padding)
                // faceBox         = padded box used internally for encoding accuracy
                DlibBiometrics.FaceBox reportedFaceBox;

                if (clientFaceBox != null
                    && clientFaceBox.Width > 20
                    && clientFaceBox.Height > 20)
                {
                    // Return the CLIENT box as-is — client already knows this position.
                    // Returning the padded box caused a feedback loop: padded box sent
                    // back next frame → padded again → box grew ~20% per scan cycle.
                    reportedFaceBox = clientFaceBox;

                    // Padding used ONLY for encoding context — wider crop = better embedding
                    var padX = Math.Max(6, (int)(clientFaceBox.Width  * 0.10));
                    var padY = Math.Max(6, (int)(clientFaceBox.Height * 0.12));
                    var left = Math.Max(0, clientFaceBox.Left - padX);
                    var top  = Math.Max(0, clientFaceBox.Top  - padY);
                    faceBox = new DlibBiometrics.FaceBox
                    {
                        Left   = left,
                        Top    = top,
                        Width  = Math.Min(imageWidth  - left, clientFaceBox.Width  + padX * 2),
                        Height = Math.Min(imageHeight - top,  clientFaceBox.Height + padY * 2)
                    };
                    faceLocation = new Location(
                        faceBox.Left, faceBox.Top,
                        faceBox.Left + faceBox.Width,
                        faceBox.Top  + faceBox.Height);
                }
                else if (!dlib.TryDetectSingleFaceFromBitmap(
                    bitmap, out faceBox, out faceLocation, out detectErr))
                {
                    return new EnrollmentScanResult
                    {
                        Ok = false, Error = detectErr ?? "NO_FACE",
                        TimingMs = sw.ElapsedMilliseconds
                    };
                }
                else
                {
                    // Server-detected box — return this directly (no padding applied)
                    reportedFaceBox = faceBox;
                }

                // ── Step 4: Sharpness — single-threaded, from Bitmap ─────────
                var sharpness = FaceQualityAnalyzer.CalculateSharpnessFromBitmap(bitmap, faceBox);
                var sharpTh   = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);

                // ── Step 5: Pre-extract RGB bytes (THREAD SAFETY) ───────────
                // Bitmap.LockBits and Bitmap.Clone are NOT thread-safe when called
                // concurrently on the same Bitmap instance. Pre-convert to a raw byte
                // array here (single-threaded), then the encoding thread works only on
                // the byte array — it never touches the Bitmap at all.
                // The liveness thread is the only one that then accesses the Bitmap,
                // so there is no concurrent pixel access.
                byte[] rgbData;
                try
                {
                    rgbData = DlibBiometrics.ExtractRgbData(bitmap);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("[EnrollmentScan] RGB extract failed: " + ex.Message);
                    return new EnrollmentScanResult
                    {
                        Ok = false, Error = "BITMAP_CONVERT_FAIL",
                        TimingMs = sw.ElapsedMilliseconds
                    };
                }

                // ── Step 6: Parallel — liveness (Bitmap) + encode (rgbData) ──
                // Thread A (liveness): ScoreFromBitmap — does bitmap.Clone() then
                //   LockBits on the CLONE only. Original bitmap not locked.
                // Thread B (encoding): TryEncodeWithLandmarksFromRgbData — only
                //   accesses rgbData byte array. Never calls LockBits on bitmap.
                // These two are fully independent — zero shared mutable state.
                double[] encoding   = null;
                float[]  landmarks5 = null;
                string   encErr     = null;
                bool     liveOk     = false;
                float?   liveProb   = null;

                Parallel.Invoke(
                    () =>
                    {
                        var live = new OnnxLiveness();
                        var r = live.ScoreFromBitmap(bitmap, faceBox);
                        liveOk   = r.Ok;
                        liveProb = r.Probability;
                    },
                    () =>
                    {
                        dlib.TryEncodeWithLandmarksFromRgbData(
                            rgbData, imageWidth, imageHeight, faceLocation,
                            out encoding, out landmarks5, out encErr);
                    }
                );

                if (encoding == null)
                    return new EnrollmentScanResult
                    {
                        Ok = false, Error = encErr ?? "ENCODING_FAIL",
                        TimingMs = sw.ElapsedMilliseconds
                    };

                var liveTh    = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var liveScore = liveProb ?? 0f;

                return new EnrollmentScanResult
                {
                    Ok             = true,
                    Base64Encoding = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(encoding)),
                    FaceEncoding   = encoding,
                    Landmarks5     = landmarks5,
                    LivenessScore  = liveScore,
                    LivenessOk     = liveOk && liveScore >= liveTh,
                    Sharpness      = sharpness,
                    SharpnessThreshold = sharpTh,
                    FaceBox        = reportedFaceBox,  // original — not padded, no feedback loop
                    ImageWidth     = imageWidth,
                    ImageHeight    = imageHeight,
                    TimingMs       = sw.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// OPTIMIZED: Fast scan with single-decode + parallel liveness + encoding
        /// 
        /// Flow:
        /// 1. Load image into Bitmap once (single JPEG decode)
        /// 2. Detect face using DlibBiometrics pool (bitmap-based)
        /// 3. Run liveness + encoding IN PARALLEL (both reuse same bitmap)
        /// 4. Return result
        /// 
        /// Performance: ~30-40% faster than file-based pipeline
        /// </summary>
        public static ScanResult ScanInMemory(HttpPostedFileBase image, bool includeTimings = false)
        {
            var sw = Stopwatch.StartNew();
            var timings = includeTimings ? new Dictionary<string, long>() : null;

            try
            {
                // STEP 1: Load image into memory ONCE (single decode)
                Bitmap bitmap = null;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        image.InputStream.CopyTo(ms);
                        ms.Position = 0;
                        bitmap = new Bitmap(ms);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("[FastScanPipeline] Failed to load image: " + ex.Message);
                    return new ScanResult 
                    { 
                        Ok = false, 
                        Error = "IMAGE_LOAD_FAIL",
                        Timings = timings,
                        TimingMs = sw.ElapsedMilliseconds
                    };
                }

                RecordTiming(timings, "load_decode", sw);

                using (bitmap)
                {
                    // STEP 2: Detect face using bitmap (no re-decode)
                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    Location faceLocation;
                    string detectErr;
                    
                    if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLocation, out detectErr))
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

                    // Compute sharpness on face ROI using bitmap (no temp file needed)
                    float sharpness = FaceQualityAnalyzer.CalculateSharpnessFromBitmap(bitmap, faceBox);
                    RecordTiming(timings, "sharpness_ms", sw);

                    // STEP 3: PARALLEL - Liveness + Encoding (both reuse same bitmap)
                    double[] encoding = null;
                    bool livenessOk = false;
                    float? livenessProb = null;

                    Parallel.Invoke(
                        () =>
                        {
                            // Face encoding using bitmap (reuses loaded image)
                            string encErr;
                            dlib.TryEncodeFromBitmapWithLocation(bitmap, faceLocation, out encoding, out encErr);
                        },
                        () =>
                        {
                            // Liveness check using bitmap (reuses loaded image)
                            var live = new OnnxLiveness();
                            var result = live.ScoreFromBitmap(bitmap, faceBox);
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
                        Sharpness = sharpness,
                        ImageWidth = bitmap.Width,  // For angle-aware tolerance
                        Timings = timings,
                        TimingMs = sw.ElapsedMilliseconds
                    };
                }
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
        }

        /// <summary>
        /// FALLBACK: File-based scan for compatibility with legacy code paths.
        /// Use ScanInMemory for better performance.
        /// </summary>
        public static ScanResult ScanFromFile(string imagePath, bool includeTimings = false)
        {
            var sw = Stopwatch.StartNew();
            var timings = includeTimings ? new Dictionary<string, long>() : null;

            try
            {
                // STEP 1: Load image into Bitmap once
                Bitmap bitmap;
                try
                {
                    bitmap = new Bitmap(imagePath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError("[FastScanPipeline] Failed to load file: " + ex.Message);
                    return new ScanResult 
                    { 
                        Ok = false, 
                        Error = "IMAGE_LOAD_FAIL",
                        Timings = timings,
                        TimingMs = sw.ElapsedMilliseconds
                    };
                }

                RecordTiming(timings, "load_decode", sw);

                using (bitmap)
                {
                    // STEP 2: Detect face
                    var dlib = new DlibBiometrics();
                    DlibBiometrics.FaceBox faceBox;
                    Location faceLocation;
                    string detectErr;
                    
                    if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLocation, out detectErr))
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
                            string encErr;
                            dlib.TryEncodeFromBitmapWithLocation(bitmap, faceLocation, out encoding, out encErr);
                        },
                        () =>
                        {
                            var live = new OnnxLiveness();
                            var result = live.ScoreFromBitmap(bitmap, faceBox);
                            livenessOk = result.Ok;
                            livenessProb = result.Probability;
                        }
                    );
                    RecordTiming(timings, "parallel_liveness_encode", sw);

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
        }

        /// <summary>
        /// OPTIMIZED: Process a single frame for mobile burst mode.
        /// Returns null if face not found or liveness failed.
        /// Uses single-decode pipeline for maximum speed.
        /// </summary>
        public static SingleFrameResult ProcessSingleFrame(
            HttpPostedFileBase image, 
            DlibBiometrics.FaceBox clientFaceBox,
            double livenessThreshold,
            double attendanceTol)
        {
            Bitmap bitmap = null;
            
            try
            {
                // Load image once
                using (var ms = new MemoryStream())
                {
                    image.InputStream.CopyTo(ms);
                    ms.Position = 0;
                    bitmap = new Bitmap(ms);
                }
            }
            catch
            {
                return null;
            }

            using (bitmap)
            {
                var dlib = new DlibBiometrics();
                var liveness = new OnnxLiveness();
                
                DlibBiometrics.FaceBox faceBox;
                Location faceLoc;
                string detectErr;
                bool usedClientBox = false;

                // Try client box first
                if (clientFaceBox != null && clientFaceBox.Width > 20 && clientFaceBox.Height > 20)
                {
                    // Build Location from client box with padding
                    var padX = Math.Max(6, (int)Math.Round(clientFaceBox.Width * 0.10));
                    var padY = Math.Max(6, (int)Math.Round(clientFaceBox.Height * 0.12));

                    faceBox = new DlibBiometrics.FaceBox
                    {
                        Left = Math.Max(0, clientFaceBox.Left - padX),
                        Top = Math.Max(0, clientFaceBox.Top - padY),
                        Width = Math.Max(1, clientFaceBox.Width + (padX * 2)),
                        Height = Math.Max(1, clientFaceBox.Height + (padY * 2))
                    };

                    faceLoc = new Location(
                        faceBox.Left,
                        faceBox.Top,
                        faceBox.Left + faceBox.Width,
                        faceBox.Top + faceBox.Height);

                    usedClientBox = true;
                }
                else
                {
                    // Server-side detection
                    if (!dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLoc, out detectErr))
                    {
                        return null;
                    }
                }

                // Liveness check
                var scored = liveness.ScoreFromBitmap(bitmap, faceBox);
                
                // Retry with server detection if client box liveness failed
                if ((!scored.Ok || (scored.Probability ?? 0) < livenessThreshold) && usedClientBox)
                {
                    if (dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLoc, out detectErr))
                    {
                        usedClientBox = false;
                        scored = liveness.ScoreFromBitmap(bitmap, faceBox);
                    }
                }

                if (!scored.Ok || (scored.Probability ?? 0) < livenessThreshold)
                    return null;

                // Encode
                double[] vec;
                string encErr;
                var encodeOk = dlib.TryEncodeFromBitmapWithLocation(bitmap, faceLoc, out vec, out encErr) && vec != null;
                
                // Retry encoding with fresh detection if needed
                if (!encodeOk && usedClientBox)
                {
                    if (dlib.TryDetectSingleFaceFromBitmap(bitmap, out faceBox, out faceLoc, out detectErr))
                    {
                        usedClientBox = false;
                        encodeOk = dlib.TryEncodeFromBitmapWithLocation(bitmap, faceLoc, out vec, out encErr) && vec != null;
                    }
                }

                if (!encodeOk)
                    return null;

                // Match
                var matchResult = FastFaceMatcher.FindBestMatch(vec, attendanceTol);
                if (matchResult == null)
                    return null;

                return new SingleFrameResult
                {
                    EmployeeId = matchResult.IsMatch ? matchResult.Employee?.EmployeeId : null,
                    Confidence = matchResult.Confidence,
                    Distance = matchResult.Distance,
                    IsMatch = matchResult.IsMatch,
                    LivenessScore = scored.Probability ?? 0,
                    UsedClientBox = usedClientBox,
                    FaceEncoding = vec,
                    ImageWidth = bitmap.Width  // FIX-04: for angle-aware tolerance
                };
            }
        }

        /// <summary>
        /// Result from processing a single frame in burst mode
        /// </summary>
        public class SingleFrameResult
        {
            public string EmployeeId { get; set; }
            public double Confidence { get; set; }
            public double Distance { get; set; }
            public bool IsMatch { get; set; }
            public double LivenessScore { get; set; }
            public bool UsedClientBox { get; set; }
            public double[] FaceEncoding { get; set; }
            public int ImageWidth { get; set; }  // FIX-04: Actual image width for angle-aware tolerance
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
