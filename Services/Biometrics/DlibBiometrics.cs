using System;
using System.Collections.Concurrent;
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
    /// Nagbibigay ng face detection at encoding gamit ang FaceRecognitionDotNet (DLib).
    ///
    /// PHASE 2 FIX (P-01): Instance Pool Pattern — pinalitan ang single global lock.
    ///
    /// PROBLEMA DATI:
    ///   Isang static FaceRecognition instance lang ang ginagamit, na naka-lock sa
    ///   buong tagal ng inference. Ibig sabihin, lahat ng concurrent scans ay
    ///   nagpipila — isa-isa silang nare-serve. Sa 10 users = 12 segundo ang hihintayin
    ///   ng huli. Sa 30 users = 36 segundo = HTTP timeout na.
    ///
    /// SOLUSYON NGAYON:
    ///   Pool ng N instances ng FaceRecognition (default: 4, configurable).
    ///   Bawat instance ay nagse-serve ng isang request sa isang pagkakataon.
    ///   Hanggang N scans ang pwedeng mag-parallel.
    ///   Ang SemaphoreSlim ay ginagamit para mag-gate ng access sa pool —
    ///   kapag puno na ang pool, naghe-hold ang request (max 30 segundo)
    ///   bago mag-timeout na may malinaw na error.
    ///
    /// MEMORY COST:
    ///   Bawat FaceRecognition instance (HOG model) ≈ 30-50 MB.
    ///   4 instances ≈ 120-200 MB dagdag na RAM — katanggap-tanggap para sa server.
    ///
    /// PAANO MAG-CONFIGURE:
    ///   Web.config: Biometrics:DlibPoolSize (default 4)
    ///               Biometrics:DlibPoolTimeoutMs (default 30000 = 30 segundo)
    ///
    ///   Kung may 8 CPU cores ang server: pwedeng itaas sa 6-8.
    ///   Kung mababa ang RAM: ibaba sa 2.
    ///
    /// NOTA:
    ///   Ang FaceRecognition.Create() ay HINDI thread-safe (gumagamit ng dlib C++ objects).
    ///   Kaya bawat instance ay sa isang thread lang ginagamit sa isang pagkakataon.
    ///   Hindi ito katulad ng OnnxLiveness kung saan ang InferenceSession ay thread-safe.
    /// </summary>
    public class DlibBiometrics
    {
        // ─── FaceBox DTO ──────────────────────────────────────────────────────────

        /// <summary>
        /// Simpleng rectangle DTO na kumakatawan sa posisyon ng mukha sa larawan.
        /// Ginagamit para ibalik ang face detection results nang hindi kailangang
        /// i-expose ang FaceRecognitionDotNet.Location type sa mga caller.
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

        // Ang pool ng FaceRecognition instances.
        // ConcurrentBag: thread-safe na "bag" ng objects — walang ordering.
        private static readonly ConcurrentBag<FaceRecognition> _pool =
            new ConcurrentBag<FaceRecognition>();

        // SemaphoreSlim: nagko-control kung gaano karaming concurrent users ang
        // maaaring kumuha ng instance mula sa pool.
        // Ini-initialize sa Application_Start pagkatapos mabasa ang config.
        private static SemaphoreSlim _semaphore;

        // Lock para sa pool initialization — iniiwasan ang double-init.
        private static readonly object _initLock = new object();
        private static volatile bool   _poolReady = false;

        // Dlib detector model — HOG (default, mas mabilis) o CNN (mas tumpak, mas mabagal).
        private static Model _model = Model.Hog;

        // Absolute path ng Dlib models directory.
        private static string _absModelsDir;

        // ─── Pool initialization ──────────────────────────────────────────────────

        /// <summary>
        /// Ini-initialize ang Dlib pool sa Application_Start.
        /// DAPAT tawagin ito ISANG BESES lang sa Global.asax bago dumating ang requests.
        ///
        /// Thread-safe: gumagamit ng double-check locking para matiyak na
        /// isang beses lang mag-initialize kahit multiple threads ang tatawag.
        /// </summary>
        public static void InitializePool()
        {
            if (_poolReady) return;

            lock (_initLock)
            {
                if (_poolReady) return;

                var poolSize   = ConfigurationService.GetInt("Biometrics:DlibPoolSize", 4);
                if (poolSize < 1)  poolSize = 1;
                if (poolSize > 16) poolSize = 16; // Safety cap — bawat instance ≈ 50 MB

                var modelsRel  = ConfigurationService.GetString(
                    "Biometrics:DlibModelsDir",
                    "~/App_Data/models/dlib");
                _absModelsDir  = HostingEnvironment.MapPath(modelsRel);

                if (string.IsNullOrWhiteSpace(_absModelsDir) || !Directory.Exists(_absModelsDir))
                    throw new InvalidOperationException(
                        "Hindi mahanap ang Dlib models directory: " + modelsRel);

                var detectorStr = ConfigurationService.GetString("Biometrics:DlibDetector", "hog");
                _model = detectorStr.Equals("cnn", StringComparison.OrdinalIgnoreCase)
                    ? Model.Cnn
                    : Model.Hog;

                // Gumawa ng pool instances.
                // TALA: Ang FaceRecognition.Create() ay matagal (naglo-load ng mga model files).
                // Kaya ginagawa ito sa Application_Start, hindi sa unang request.
                _semaphore = new SemaphoreSlim(poolSize, poolSize);

                for (int i = 0; i < poolSize; i++)
                {
                    _pool.Add(FaceRecognition.Create(_absModelsDir));
                }

                _poolReady = true;
            }
        }

        /// <summary>
        /// Constructor — tinitiyak na initialized ang pool.
        /// Para sa backward compatibility, ang existing na code na gumagamit ng
        /// "new DlibBiometrics()" ay hindi na kailangang baguhin.
        /// </summary>
        public DlibBiometrics()
        {
            EnsurePoolReady();
        }

        /// <summary>
        /// Tinitiyak na naka-initialize ang pool.
        /// Kapag hindi pa naka-initialize (e.g. hindi natawag ang InitializePool sa startup),
        /// ini-initialize ito inline — mas mabagal ang unang call pero hindi mag-crash.
        /// </summary>
        private static void EnsurePoolReady()
        {
            if (!_poolReady)
                InitializePool();
        }

        // ─── Pool acquisition ─────────────────────────────────────────────────────

        /// <summary>
        /// Kumukuha ng FaceRecognition instance mula sa pool.
        /// Naghe-hold kung puno ang pool, hanggang sa DlibPoolTimeoutMs.
        /// Nagrereturn ng null kapag nag-timeout.
        ///
        /// IMPORTANTENG GAMITIN KASAMA ANG try/finally para matiyak na
        /// naibabalik ang instance sa pool:
        ///
        ///   var fr = RentInstance();
        ///   if (fr == null) return null; // pool timeout
        ///   try { /* gamitin ang fr */ }
        ///   finally { ReturnInstance(fr); }
        /// </summary>
        private static FaceRecognition RentInstance()
        {
            EnsurePoolReady();

            var timeoutMs = ConfigurationService.GetInt("Biometrics:DlibPoolTimeoutMs", 30_000);

            // Hintayin ang available na slot sa pool.
            // Kapag nag-timeout, ibabalik ang null.
            if (!_semaphore.Wait(timeoutMs))
                return null; // Pool timeout — masyadong maraming concurrent scans.

            // Kumuha ng instance mula sa bag.
            FaceRecognition instance;
            if (!_pool.TryTake(out instance))
            {
                // Hindi dapat mangyari (may semaphore tayo), pero safety net lang.
                _semaphore.Release();
                return null;
            }

            return instance;
        }

        /// <summary>
        /// Ibinabalik ang isang instance sa pool pagkatapos gamitin.
        /// LAGING tawagin ito sa finally block para hindi maubusan ang pool.
        /// </summary>
        private static void ReturnInstance(FaceRecognition instance)
        {
            if (instance == null) return;
            _pool.Add(instance);
            _semaphore.Release();
        }

        // ─── Public inference methods ─────────────────────────────────────────────

        /// <summary>
        /// Nagde-detect ng mga mukha mula sa isang image file.
        /// Ginagamit sa admin enrollment preview at ScanFramePipeline.
        /// </summary>
        public FaceBox[] DetectFacesFromFile(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return Array.Empty<FaceBox>();

            var fr = RentInstance();
            if (fr == null)
            {
                // Nag-timeout ang pool — masyadong maraming concurrent scans.
                // Ibalik ang empty array para ang caller ay mag-handle ng NO_FACE error.
                return Array.Empty<FaceBox>();
            }

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
                // LAGING ibalik ang instance — kahit nag-throw ng exception.
                ReturnInstance(fr);
            }
        }

        /// <summary>
        /// Nagde-detect ng isang mukha at nagbibigay ng Location object
        /// para gamitin sa encoding (para hindi na kailangang mag-detect ulit).
        ///
        /// Returns false kapag:
        ///   - Walang nakitang mukha (error = "NO_FACE")
        ///   - Maraming mukha (error = "MULTI_FACE")
        ///   - Nag-timeout ang pool (error = "POOL_TIMEOUT")
        /// </summary>
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

        /// <summary>
        /// Nag-e-encode ng mukha gamit ang kilalang Location (hindi na kailangang
        /// mag-detect ulit — mas mabilis ito).
        ///
        /// Ginagamit pagkatapos ng liveness check para maiwasan ang double detection.
        /// </summary>
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

                    if (locs.Length == 0)
                    {
                        error = "NO_FACE";
                        return false;
                    }
                    if (locs.Length > 1)
                    {
                        error = "MULTI_FACE";
                        return false;
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

        //  Combined encode + landmarks (single file load, one pool rent) 

        /// <summary>
        /// Loads the image file ONCE and calls both FaceEncodings() and FaceLandmarks()
        /// inside a single pool rent. Returns the 128d embedding plus a 6-float array
        /// [leftEyeX, leftEyeY, rightEyeX, rightEyeY, noseTipX, noseTipY] in pixel coords.
        ///
        /// landmarks5 is null if landmark extraction fails  caller must handle gracefully.
        /// Encoding still succeeds in that case (best-effort landmarks).
        ///
        /// Uses PredictorModel.Small (5-point)  faster than Large (68-point).
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
                    // NOTE: FaceLandmark requires shape_predictor_5_face_landmarks.dat model.
                    // If the model is not present, this will throw an exception which we catch.
                    try
                    {
                        var landmarkResult = fr.FaceLandmark(img, locations, PredictorModel.Small, Model.Cnn).FirstOrDefault();
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
        /// Extracts a compact 6-float landmark array from a FaceLandmarks result.
        /// Works with both Small (5-pt) and Large (68-pt) predictor outputs by
        /// averaging all available points in each eye group.
        ///
        /// Returns float[6]: [leftEyeX, leftEyeY, rightEyeX, rightEyeY, noseTipX, noseTipY]
        /// All values in image pixel coordinates. Returns null on any failure.
        /// </summary>
        private static float[] ExtractLandmarks5(
            IDictionary<FacePart, IEnumerable<FacePoint>> parts)
        {
            if (parts == null) return null;

            float leX = 0f, leY = 0f, leN = 0f;
            float reX = 0f, reY = 0f, reN = 0f;
            float nX  = 0f, nY  = 0f, nN  = 0f;

            IEnumerable<FacePoint> pts;

            // Left eye  average all points in the group
            if (parts.TryGetValue(FacePart.LeftEye, out pts) && pts != null)
                foreach (var p in pts) { leX += p.Point.X; leY += p.Point.Y; leN++; }

            // Right eye  average all points in the group
            if (parts.TryGetValue(FacePart.RightEye, out pts) && pts != null)
                foreach (var p in pts) { reX += p.Point.X; reY += p.Point.Y; reN++; }

            // Nose tip preferred; bottom of nose bridge as fallback
            if (parts.TryGetValue(FacePart.NoseTip, out pts) && pts != null)
                foreach (var p in pts) { nX += p.Point.X; nY += p.Point.Y; nN++; }

            if (nN == 0 && parts.TryGetValue(FacePart.NoseBridge, out pts) && pts != null)
            {
                var noseList = pts.ToList();
                if (noseList.Count > 0)
                {
                    // Bottom of bridge is closest to nose tip
                    var last = noseList[noseList.Count - 1];
                    nX = last.Point.X; nY = last.Point.Y; nN = 1f;
                }
            }

            if (leN == 0f || reN == 0f || nN == 0f) return null;

            return new float[]
            {
                leX / leN, leY / leN,   // [0,1] left eye center
                reX / reN, reY / reN,   // [2,3] right eye center
                nX  / nN,  nY  / nN    // [4,5] nose tip
            };
        }

        // ─── Disposal ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Nililinis ang lahat ng FaceRecognition instances sa pool.
        /// Tawagin ito sa Global.asax Application_End para ma-release ang
        /// unmanaged dlib resources bago mag-shutdown ang IIS app pool.
        ///
        /// Thread-safe: ligtas na tawagin kahit may ongoing requests,
        /// kahit na magreresulta ito sa POOL_TIMEOUT errors para sa mga
        /// requests na dumating habang nag-shutdown.
        /// </summary>
        public static void DisposePool()
        {
            lock (_initLock)
            {
                _poolReady = false;

                // Alisin at i-dispose ang lahat ng instances.
                while (_pool.TryTake(out var fr))
                {
                    try { fr.Dispose(); } catch { /* best effort */ }
                }

                // I-dispose ang semaphore.
                try { _semaphore?.Dispose(); } catch { /* best effort */ }
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

        /// <summary>
        /// Kinakalkula ang Euclidean distance sa pagitan ng dalawang 128-dim vectors.
        /// Mas mababa ang distance = mas magkahawig ang mga mukha.
        /// Typical threshold: 0.60 (configurable sa Biometrics:DlibTolerance).
        /// </summary>
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

        /// <summary>
        /// Kino-convert ang 128-dim double vector papunta sa byte array
        /// para ma-store sa database (8 bytes per double = 1024 bytes total).
        /// </summary>
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

        /// <summary>
        /// Kino-convert ang byte array mula sa database papunta sa 128-dim double vector.
        /// </summary>
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
