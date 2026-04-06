using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Hosting;
using FaceAttend.Services;
using FaceRecognitionDotNet;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Face detection and encoding via FaceRecognitionDotNet (dlib HOG + ResNet).
    /// Uses a pool of N FaceRecognition instances (Biometrics:DlibPoolSize, default 4)
    /// so concurrent requests are served in parallel. Each instance is not thread-safe,
    /// so access is gated with SemaphoreSlim. Pool is initialized once at Application_Start.
    /// Config: Biometrics:DlibPoolSize, Biometrics:DlibPoolTimeoutMs (default 30 s).
    /// </summary>
    public class DlibBiometrics
    {
        // ─── FaceBox DTO ──────────────────────────────────────────────────────────

        /// <summary>
        /// Rectangle DTO wrapping face detection results without exposing FaceRecognitionDotNet.Location to callers.
        /// </summary>
        public class FaceBox
        {
            public int Left   { get; set; }
            public int Top    { get; set; }
            public int Width  { get; set; }
            public int Height { get; set; }
            
            /// <summary>
            /// Right edge coordinate (Left + Width).
            /// </summary>
            public int Right => Left + Width;
            
            /// <summary>
            /// Bottom edge coordinate (Top + Height).
            /// </summary>
            public int Bottom => Top + Height;
            
            /// <summary>
            /// Calculated area of the face box (Width * Height).
            /// Used for selecting the largest face when multiple faces are detected.
            /// </summary>
            public int Area => Width * Height;
        }

        // ─── Pool state ───────────────────────────────────────────────────────────

        private static readonly ConcurrentBag<FaceRecognition> _pool =
            new ConcurrentBag<FaceRecognition>();

        // Initialized at Application_Start after config is read.
        private static SemaphoreSlim _semaphore;

        private static readonly object _initLock = new object();
        private static volatile bool   _poolReady = false;

        // HOG (default, faster) or CNN (more accurate, slower).
        private static Model _model = Model.Hog;

        private static string _absModelsDir;

        // ─── Pool initialization ──────────────────────────────────────────────────

        /// <summary>
        /// Initializes the pool at Application_Start. Safe to call multiple times (double-check lock).
        /// </summary>
        public static void InitializePool()
        {
            if (_poolReady) return;

            lock (_initLock)
            {
                if (_poolReady) return;

                var poolSize   = ConfigurationService.GetInt("Biometrics:DlibPoolSize", 4);
                if (poolSize < 1)  poolSize = 1;
                if (poolSize > 16) poolSize = 16; // Safety cap — each instance ≈ 50 MB

                var modelsRel  = ConfigurationService.GetString(
                    "Biometrics:DlibModelsDir",
                    "~/App_Data/models/dlib");
                _absModelsDir  = HostingEnvironment.MapPath(modelsRel);

                if (string.IsNullOrWhiteSpace(_absModelsDir) || !Directory.Exists(_absModelsDir))
                    throw new InvalidOperationException(
                        "Dlib models directory not found: " + modelsRel);

                var detectorStr = ConfigurationService.GetString("Biometrics:DlibDetector", "hog");
                _model = detectorStr.Equals("cnn", StringComparison.OrdinalIgnoreCase)
                    ? Model.Cnn
                    : Model.Hog;

                _semaphore = new SemaphoreSlim(poolSize, poolSize);
                for (int i = 0; i < poolSize; i++)
                    _pool.Add(FaceRecognition.Create(_absModelsDir));

                _poolReady = true;
            }
        }

        public DlibBiometrics()
        {
            EnsurePoolReady();
        }

        private static void EnsurePoolReady()
        {
            if (!_poolReady)
                InitializePool();
        }

        // ─── Pool acquisition ─────────────────────────────────────────────────────

        // Returns null on pool timeout — always use in try/finally with ReturnInstance.
        private static FaceRecognition RentInstance()
        {
            EnsurePoolReady();

            var timeoutMs = ConfigurationService.GetInt("Biometrics:DlibPoolTimeoutMs", 30_000);

            var sw = Stopwatch.StartNew();
            if (!_semaphore.Wait(timeoutMs))
                return null;
            sw.Stop();

            if (sw.ElapsedMilliseconds > 500)
            {
                var poolSize  = ConfigurationService.GetInt("Biometrics:DlibPoolSize", 4);
                var occupancy = poolSize - _semaphore.CurrentCount;
                Trace.TraceWarning(
                    "[DlibPool] Semaphore wait: {0}ms. Pool occupancy: {1}/{2}. " +
                    "Consider increasing Biometrics:DlibPoolSize.",
                    sw.ElapsedMilliseconds, occupancy, poolSize);
            }

            FaceRecognition instance;
            if (!_pool.TryTake(out instance))
            {
                _semaphore.Release();
                return null;
            }

            return instance;
        }

        private static void ReturnInstance(FaceRecognition instance)
        {
            if (instance == null) return;
            _pool.Add(instance);
            _semaphore.Release();
        }

        // ─── Public inference methods ─────────────────────────────────────────────

        public FaceBox[] DetectFacesFromFile(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return Array.Empty<FaceBox>();

            var fr = RentInstance();
            if (fr == null) return Array.Empty<FaceBox>();

            try
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var locs = fr.FaceLocations(
                        img,
                        numberOfTimesToUpsample: 0,
                        model: _model).ToArray();

                    return locs.Select(loc => new FaceBox
                    {
                        Left   = loc.Left,
                        Top    = loc.Top,
                        Width  = Math.Max(0, loc.Right  - loc.Left),
                        Height = Math.Max(0, loc.Bottom - loc.Top)
                    }).ToArray();
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        // Detects exactly one face. Returns false with error = NO_FACE / MULTI_FACE / POOL_TIMEOUT.
        public bool TryDetectSingleFaceFromFile(
            string       imagePath,
            out FaceBox  faceBox,
            out Location faceLocation,
            out string   error)
        {
            return TryDetectFaceFromFile(
                imagePath,
                out faceBox,
                out faceLocation,
                out error,
                allowLargestFace: false,
                primaryUpsample: 0,
                retryUpsampleOnNoFace: false);
        }

        /// <summary>
        /// Detects a face with kiosk-friendly fallbacks.
        ///
        /// Differences from TryDetectSingleFaceFromFile:
        ///   - Can choose the largest face instead of failing on MULTI_FACE.
        ///   - Can retry once with 1 upsample when the first pass finds no face.
        ///
        /// This is useful for mobile kiosk bursts where the face may be slightly
        /// smaller or softer than admin enrollment images.
        /// </summary>
        public bool TryDetectBestFaceFromFile(
            string       imagePath,
            out FaceBox  faceBox,
            out Location faceLocation,
            out string   error,
            bool         allowLargestFace = true,
            int          primaryUpsample = 0,
            bool         retryUpsampleOnNoFace = true)
        {
            return TryDetectFaceFromFile(
                imagePath,
                out faceBox,
                out faceLocation,
                out error,
                allowLargestFace,
                primaryUpsample,
                retryUpsampleOnNoFace);
        }

        private bool TryDetectFaceFromFile(
            string       imagePath,
            out FaceBox  faceBox,
            out Location faceLocation,
            out string   error,
            bool         allowLargestFace,
            int          primaryUpsample,
            bool         retryUpsampleOnNoFace)
        {
            faceBox      = null;
            faceLocation = default(Location);
            error        = null;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "BAD_IMAGE_PATH";
                return false;
            }

            if (primaryUpsample < 0)
                primaryUpsample = 0;

            var fr = RentInstance();
            if (fr == null)
            {
                error = "POOL_TIMEOUT";
                return false;
            }

            try
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var locs = fr.FaceLocations(
                        img,
                        numberOfTimesToUpsample: primaryUpsample,
                        model: _model).ToArray();

                    if (locs.Length == 0 && retryUpsampleOnNoFace)
                    {
                        locs = fr.FaceLocations(
                            img,
                            numberOfTimesToUpsample: Math.Max(1, primaryUpsample + 1),
                            model: _model).ToArray();
                    }

                    if (locs.Length == 0)
                    {
                        error = "NO_FACE";
                        return false;
                    }

                    Location loc;
                    if (locs.Length == 1)
                    {
                        loc = locs[0];
                    }
                    else if (allowLargestFace)
                    {
                        loc = locs
                            .OrderByDescending(l => Math.Max(0, l.Right - l.Left) * Math.Max(0, l.Bottom - l.Top))
                            .First();
                    }
                    else
                    {
                        error = "MULTI_FACE";
                        return false;
                    }

                    faceLocation = loc;
                    faceBox      = new FaceBox
                    {
                        Left   = loc.Left,
                        Top    = loc.Top,
                        Width  = Math.Max(0, loc.Right  - loc.Left),
                        Height = Math.Max(0, loc.Bottom - loc.Top)
                    };

                    return true;
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        public bool TryEncodeFromFileWithLocation(
            string       imagePath,
            Location     faceLocation,
            out double[] embedding,
            out string   error)
        {
            embedding = null;
            error     = null;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "BAD_IMAGE_PATH";
                return false;
            }

            var fr = RentInstance();
            if (fr == null)
            {
                error = "POOL_TIMEOUT";
                return false;
            }

            try
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var enc = fr.FaceEncodings(img, new[] { faceLocation })
                               .FirstOrDefault();

                    if (enc == null) { error = "ENCODING_FAIL"; return false; }

                    embedding = enc.GetRawEncoding();
                    return true;
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        // ─── OPTIMIZED: Bitmap-based methods (single-decode pipeline) ─────────────

        /// <summary>
        /// Convert Bitmap to RGB byte array for FaceRecognitionDotNet.
        /// Handles BGR to RGB conversion and stride padding.
        /// </summary>
        private static byte[] BitmapToRgb(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            var rgb = new byte[width * height * 3];

            var rect = new Rectangle(0, 0, width, height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            try
            {
                int stride = bmpData.Stride;
                int rowPadding = stride - (width * 3);
                int srcRowSize = width * 3;
                
                // Read entire bitmap data
                int totalBytes = Math.Abs(stride) * height;
                var rowBuffer = new byte[totalBytes];
                Marshal.Copy(bmpData.Scan0, rowBuffer, 0, totalBytes);

                // Convert BGR (with padding) to RGB (no padding)
                int srcIdx = 0;
                int dstIdx = 0;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // BGR to RGB
                        rgb[dstIdx + 2] = rowBuffer[srcIdx + 0]; // B -> R
                        rgb[dstIdx + 1] = rowBuffer[srcIdx + 1]; // G -> G  
                        rgb[dstIdx + 0] = rowBuffer[srcIdx + 2]; // R -> B
                        srcIdx += 3;
                        dstIdx += 3;
                    }
                    srcIdx += rowPadding; // Skip stride padding
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }

            return rgb;
        }

        /// <summary>
        /// OPTIMIZED: Detect face from already-loaded Bitmap — avoids double JPEG decode.
        /// Returns FaceBox and FaceRecognitionDotNet.Location for encoding.
        /// </summary>
        public bool TryDetectSingleFaceFromBitmap(
            Bitmap       bitmap,
            out FaceBox  faceBox,
            out Location faceLocation,
            out string   error)
        {
            faceBox      = null;
            faceLocation = default(Location);
            error        = null;

            if (bitmap == null)
            {
                error = "NO_BITMAP";
                return false;
            }

            var fr = RentInstance();
            if (fr == null)
            {
                error = "POOL_TIMEOUT";
                return false;
            }

            try
            {
                // Convert bitmap to RGB array and load into FaceRecognition
                var rgbData = BitmapToRgb(bitmap);
                using (var img = FaceRecognition.LoadImage(rgbData, bitmap.Height, bitmap.Width, bitmap.Width * 3, Mode.Rgb))
                {
                    var locs = fr.FaceLocations(
                        img,
                        numberOfTimesToUpsample: 0,
                        model: _model).ToArray();

                    // Retry with upsample=1 when no face found on first pass.
                    // Mobile cameras produce smaller/softer images that need
                    // upsampling to detect reliably — mirrors TryDetectBestFaceFromFile.
                    if (locs.Length == 0)
                    {
                        locs = fr.FaceLocations(
                            img,
                            numberOfTimesToUpsample: 1,
                            model: _model).ToArray();
                    }

                    if (locs.Length == 0)
                    {
                        error = "NO_FACE";
                        return false;
                    }

                    // Pick largest face instead of hard-failing on multi-face —
                    // matches allowLargestFace behaviour of TryDetectBestFaceFromFile.
                    if (locs.Length > 1)
                    {
                        locs = new[] {
                            locs.OrderByDescending(l =>
                                Math.Max(0, l.Right - l.Left) *
                                Math.Max(0, l.Bottom - l.Top))
                            .First()
                        };
                    }

                    var loc = locs[0];
                    faceLocation = loc;
                    faceBox = new FaceBox
                    {
                        Left   = loc.Left,
                        Top    = loc.Top,
                        Width  = Math.Max(0, loc.Right  - loc.Left),
                        Height = Math.Max(0, loc.Bottom - loc.Top)
                    };
                    return true;
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        /// <summary>
        /// OPTIMIZED: Encode face from already-loaded Bitmap — avoids double JPEG decode.
        /// Requires the Location from a previous detection call.
        /// </summary>
        public bool TryEncodeFromBitmapWithLocation(
            Bitmap       bitmap,
            Location     faceLocation,
            out double[] embedding,
            out string   error)
        {
            embedding = null;
            error     = null;

            if (bitmap == null)
            {
                error = "NO_BITMAP";
                return false;
            }

            var fr = RentInstance();
            if (fr == null)
            {
                error = "POOL_TIMEOUT";
                return false;
            }

            try
            {
                // Convert bitmap to RGB array and load into FaceRecognition
                var rgbData = BitmapToRgb(bitmap);
                using (var img = FaceRecognition.LoadImage(rgbData, bitmap.Height, bitmap.Width, bitmap.Width * 3, Mode.Rgb))
                {
                    var enc = fr.FaceEncodings(img, new[] { faceLocation })
                               .FirstOrDefault();

                    if (enc == null) { error = "ENCODING_FAIL"; return false; }

                    embedding = enc.GetRawEncoding();
                    return true;
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        // ─── Combined encode + landmarks from Bitmap (no file I/O) ───────────────

        /// <summary>
        /// Bitmap version of TryEncodeWithLandmarks — zero file I/O.
        /// Loads the Bitmap's RGB data once and calls both FaceEncodings()
        /// and FaceLandmarks() inside a single pool rent.
        ///
        /// Used by FastScanPipeline.EnrollmentScanInMemory for the parallel
        /// encode+landmarks thread. Liveness runs concurrently on the same Bitmap.
        /// </summary>
        public bool TryEncodeWithLandmarksFromBitmap(
            Bitmap       bitmap,
            Location     faceLocation,
            out double[] embedding,
            out float[]  landmarks5,
            out string   error)
        {
            embedding  = null;
            landmarks5 = null;
            error      = null;

            if (bitmap == null) { error = "NO_BITMAP"; return false; }

            var fr = RentInstance();
            if (fr == null) { error = "POOL_TIMEOUT"; return false; }

            try
            {
                var rgbData = BitmapToRgb(bitmap);
                using (var img = FaceRecognition.LoadImage(
                    rgbData, bitmap.Height, bitmap.Width, bitmap.Width * 3, Mode.Rgb))
                {
                    var locations = new[] { faceLocation };

                    // Encoding — primary output, must succeed
                    var enc = fr.FaceEncodings(img, locations).FirstOrDefault();
                    if (enc == null) { error = "ENCODING_FAIL"; return false; }
                    embedding = enc.GetRawEncoding();

                    // Landmarks — best-effort, failure must NOT abort encoding
                    try
                    {
                        // Use FaceLandmark (singular) with Small predictor
                        // PredictorModel.Large = shape_predictor_68_face_landmarks.dat
                        // This is reliably loaded by FaceRecognition.Create(modelsDir).
                        // PredictorModel.Small (5-point) fails silently on many builds.
                        var lmSets = fr.FaceLandmark(img, locations, PredictorModel.Large)
                                       .FirstOrDefault();
                        if (lmSets != null)
                            landmarks5 = ExtractLandmarks5(lmSets);
                    }
                    catch
                    {
                        // shape_predictor_5_face_landmarks.dat missing or other error
                        // landmarks5 stays null — pose falls back to box geometry
                        landmarks5 = null;
                    }

                    return true;
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        // ─── Public helper: extract RGB bytes from Bitmap ─────────────────────────

        /// <summary>
        /// Public wrapper around BitmapToRgb. Converts bitmap to a flat RGB byte array
        /// [R,G,B, R,G,B, ...] row-major. Called by FastScanPipeline before Parallel.Invoke
        /// to pre-extract pixel data so encoding thread never touches the original Bitmap.
        /// </summary>
        public static byte[] ExtractRgbData(Bitmap bitmap)
        {
            return BitmapToRgb(bitmap);
        }

        /// <summary>
        /// Encode + landmarks from pre-computed RGB bytes — zero Bitmap access.
        /// Thread-safe: works on a byte array, never calls LockBits or Clone.
        /// Used by the parallel encoding thread so it never races with the liveness
        /// thread which may still be reading the original Bitmap.
        /// </summary>
        public bool TryEncodeWithLandmarksFromRgbData(
            byte[]   rgbData,
            int      imageWidth,
            int      imageHeight,
            Location faceLocation,
            out double[] embedding,
            out float[]  landmarks5,
            out string   error)
        {
            embedding  = null;
            landmarks5 = null;
            error      = null;

            if (rgbData == null || rgbData.Length == 0) { error = "NO_RGB_DATA"; return false; }

            var fr = RentInstance();
            if (fr == null) { error = "POOL_TIMEOUT"; return false; }

            try
            {
                using (var img = FaceRecognition.LoadImage(
                    rgbData, imageHeight, imageWidth, imageWidth * 3, Mode.Rgb))
                {
                    var locations = new[] { faceLocation };

                    var enc = fr.FaceEncodings(img, locations).FirstOrDefault();
                    if (enc == null) { error = "ENCODING_FAIL"; return false; }
                    embedding = enc.GetRawEncoding();

                    try
                    {
                        // PredictorModel.Large = shape_predictor_68_face_landmarks.dat
                        // This is reliably loaded by FaceRecognition.Create(modelsDir).
                        // PredictorModel.Small (5-point) fails silently on many builds.
                        var lmSets = fr.FaceLandmark(img, locations, PredictorModel.Large)
                                       .FirstOrDefault();
                        if (lmSets != null)
                            landmarks5 = ExtractLandmarks5(lmSets);
                    }
                    catch { landmarks5 = null; }

                    return true;
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        // ─── Combined encode + landmarks (single file load, one pool rent) ─────────

        /// <summary>
        /// Loads the image file ONCE and calls both FaceEncodings() and FaceLandmarks()
        /// inside a single pool rent. Returns the 128d embedding plus a 6-float array
        /// [leftEyeX, leftEyeY, rightEyeX, rightEyeY, noseTipX, noseTipY] in pixel coords.
        ///
        /// landmarks5 is null if landmark extraction fails  caller must handle gracefully.
        /// Encoding still succeeds in that case (best-effort landmarks).
        ///
        /// Uses PredictorModel.Large (68-point) for reliable landmark extraction.
        /// Works with both small and large predictors via FacePart key inspection.
        /// </summary>
        public bool TryEncodeWithLandmarks(
            string       imagePath,
            Location     faceLocation,
            out double[] embedding,
            out float[]  landmarks5,
            out string   error)
        {
            embedding  = null;
            landmarks5 = null;
            error      = null;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "BAD_IMAGE_PATH";
                return false;
            }

            var fr = RentInstance();
            if (fr == null)
            {
                error = "POOL_TIMEOUT";
                return false;
            }

            try
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var locations = new[] { faceLocation };

                    // Encoding (must succeed  this is the primary output)
                    var enc = fr.FaceEncodings(img, locations).FirstOrDefault();
                    if (enc == null) { error = "ENCODING_FAIL"; return false; }
                    embedding = enc.GetRawEncoding();

                    // Landmarks (best-effort  failure does not abort encoding)
                    // NOTE: FaceLandmarks requires shape_predictor_5_face_landmarks.dat model.
                    // If the model is not present, this will throw an exception which we catch.
                    try
                    {
                        // Use FaceLandmark (singular) with Small predictor — consistent with pool init
                        // PredictorModel.Large = shape_predictor_68_face_landmarks.dat
                        // This is reliably loaded by FaceRecognition.Create(modelsDir).
                        // PredictorModel.Small (5-point) fails silently on many builds.
                        var landmarkResult = fr.FaceLandmark(img, locations, PredictorModel.Large)
                                              .FirstOrDefault();
                        if (landmarkResult != null)
                            landmarks5 = ExtractLandmarks5(landmarkResult);
                    }
                    catch
                    {
                        // Model file not present or other error - landmarks5 stays null
                        landmarks5 = null;
                    }

                    return true;
                }
            }
            finally
            {
                ReturnInstance(fr);
            }
        }

        /// <summary>
        /// Extracts landmark data from a 68-point (or 5-point) predictor result.
        /// Returns float[8]: [leftEyeX, leftEyeY, rightEyeX, rightEyeY,
        ///                    noseTipX, noseTipY, chinX, chinY]
        ///
        /// Chin is extracted from FacePart.Chin (jaw points 0-16 in dlib).
        /// The chin TIP is the point with the maximum Y value (lowest in image)
        /// which is the bottom-center of the jaw.
        ///
        /// With 68-point model: all 8 values are populated.
        /// With 5-point model: chinX/chinY may be 0 if FacePart.Chin not available.
        /// Returns null if eye or nose data is missing.
        /// </summary>
        private static float[] ExtractLandmarks5(
            IDictionary<FacePart, IEnumerable<FacePoint>> parts)
        {
            if (parts == null) return null;

            float leX = 0f, leY = 0f, leN = 0f;
            float reX = 0f, reY = 0f, reN = 0f;
            float nX  = 0f, nY  = 0f, nN  = 0f;
            float chinX = 0f, chinY = 0f;

            IEnumerable<FacePoint> pts;

            // Left eye  average all points in group (6 pts for 68-model, 2 for 5-model)
            if (parts.TryGetValue(FacePart.LeftEye, out pts) && pts != null)
                foreach (var p in pts) { leX += p.Point.X; leY += p.Point.Y; leN++; }

            // Right eye  average all points in group
            if (parts.TryGetValue(FacePart.RightEye, out pts) && pts != null)
                foreach (var p in pts) { reX += p.Point.X; reY += p.Point.Y; reN++; }

            // Nose tip (FacePart.NoseTip = nostrils area, points 31-35 in 68-model)
            // Average all nose tip points for stability
            if (parts.TryGetValue(FacePart.NoseTip, out pts) && pts != null)
                foreach (var p in pts) { nX += p.Point.X; nY += p.Point.Y; nN++; }

            // Nose bridge fallback (points 27-30, top of nose near forehead)
            if (nN == 0 && parts.TryGetValue(FacePart.NoseBridge, out pts) && pts != null)
            {
                var noseList = pts.ToList();
                if (noseList.Count > 0)
                {
                    // Use bottom of bridge (closest to nose tip = highest Y value)
                    var bottomBridge = noseList.OrderByDescending(p => p.Point.Y).First();
                    nX = bottomBridge.Point.X; nY = bottomBridge.Point.Y; nN = 1f;
                }
            }

            if (leN == 0f || reN == 0f || nN == 0f) return null;

            // Chin  FacePart.Chin contains the full jaw outline (points 0-16 in 68-model)
            // The chin TIP = maximum Y value (lowest point in image = bottom of chin)
            if (parts.TryGetValue(FacePart.Chin, out pts) && pts != null)
            {
                float maxY = float.MinValue;
                float bestX = 0f;
                foreach (var p in pts)
                {
                    if (p.Point.Y > maxY)
                    {
                        maxY = p.Point.Y;
                        bestX = p.Point.X;
                    }
                }
                chinX = bestX;
                chinY = maxY;
            }

            return new float[]
            {
                leX / leN, leY / leN,   // [0,1] left eye center (person's left = image right)
                reX / reN, reY / reN,   // [2,3] right eye center (person's right = image left)
                nX  / nN,  nY  / nN,   // [4,5] nose tip
                chinX,     chinY        // [6,7] chin bottom tip (0,0 if unavailable)
            };
        }

        // ─── Disposal ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Disposes all pool instances. Call from Application_End to release unmanaged dlib resources.
        /// </summary>
        public static void DisposePool()
        {
            lock (_initLock)
            {
                _poolReady = false;
                while (_pool.TryTake(out var fr))
                    try { fr.Dispose(); } catch { }
                try { _semaphore?.Dispose(); } catch { }
                _semaphore = null;
            }
        }

        /// <summary>
        /// Returns current pool status for diagnostics.
        /// </summary>
        public static object GetPoolStatus()
        {
            return new
            {
                poolReady = _poolReady,
                modelsDir = _absModelsDir,
                currentCount = _pool?.Count ?? 0,
                detectorModel = _model.ToString()
            };
        }

        // ─── Static helpers ───────────────────────────────────────────────────────

        /// <summary>Euclidean distance between two 128-d face vectors. Lower = more similar.</summary>
        public static double Distance(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return double.PositiveInfinity;

            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i] - b[i];
                sum += d * d;
            }
            return Math.Sqrt(sum);
        }

        /// <summary>Serializes a 128-d vector to a 1024-byte array for DB storage.</summary>
        public static byte[] EncodeToBytes(double[] v)
        {
            if (v == null || v.Length != 128) return null;
            var bytes = new byte[128 * 8];
            for (int i = 0; i < 128; i++)
            {
                var b = BitConverter.GetBytes(v[i]);
                Buffer.BlockCopy(b, 0, bytes, i * 8, 8);
            }
            return bytes;
        }

        /// <summary>Deserializes a 1024-byte DB blob back to a 128-d vector.</summary>
        public static double[] DecodeFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 128 * 8) return null;
            var v = new double[128];
            for (int i = 0; i < 128; i++)
                v[i] = BitConverter.ToDouble(bytes, i * 8);
            return v;
        }
    }
}
