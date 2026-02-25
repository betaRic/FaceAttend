using System;
using System.IO;
using System.Linq;
using System.Web.Hosting;
using FaceAttend.Services;
using FaceRecognitionDotNet;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Wraps FaceRecognitionDotNet for face detection and encoding.
    ///
    /// Fix applied vs. original:
    ///   The static FaceRecognition instance <c>_fr</c> was never disposed on app shutdown,
    ///   leaking unmanaged DLib resources when IIS recycles the app pool.
    ///   Added <see cref="DisposeInstance"/> to be called from Global.asax Application_End.
    ///
    /// NOTE on static lock scope:
    ///   Both DetectFacesFromFile and GetSingleFaceEncodingFromFile hold _lock for the
    ///   entire inference duration.  For a single-kiosk deployment this is fine.
    ///   For multi-threaded deployments with concurrent scans, consider creating one
    ///   DlibBiometrics instance per request (each with its own FaceRecognition snapshot)
    ///   or upgrading to a reader-writer or pool pattern.
    /// </summary>
    public class DlibBiometrics
    {
        public class FaceBox
        {
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private static readonly object _lock = new object();
        private static FaceRecognition _fr;
        private static Model _model;

        public DlibBiometrics()
        {
            EnsureInit();
        }

        private static void EnsureInit()
        {
            if (_fr != null) return;

            lock (_lock)
            {
                if (_fr != null) return;

                var modelsDir  = AppSettings.GetString("Biometrics:DlibModelsDir", "~/App_Data/models/dlib");
                var detector   = AppSettings.GetString("Biometrics:DlibDetector", "hog");
                var absModelsDir = HostingEnvironment.MapPath(modelsDir);

                if (string.IsNullOrWhiteSpace(absModelsDir) || !Directory.Exists(absModelsDir))
                    throw new InvalidOperationException("Dlib models directory not found: " + modelsDir);

                _fr    = FaceRecognition.Create(absModelsDir);
                _model = detector.Equals("cnn", StringComparison.OrdinalIgnoreCase)
                    ? Model.Cnn
                    : Model.Hog;
            }
        }

        /// <summary>
        /// Disposes the static FaceRecognition instance.
        /// Call from <c>Global.asax Application_End</c>:
        /// <code>
        ///   protected void Application_End()
        ///   {
        ///       FaceAttend.Services.Biometrics.DlibBiometrics.DisposeInstance();
        ///       FaceAttend.Services.Biometrics.OnnxLiveness.DisposeSession();
        ///   }
        /// </code>
        /// </summary>
        public static void DisposeInstance()
        {
            lock (_lock)
            {
                if (_fr != null)
                {
                    try { _fr.Dispose(); } catch { /* best effort */ }
                    _fr = null;
                }
            }
        }

        public FaceBox[] DetectFacesFromFile(string imagePath)
        {
            EnsureInit();
            lock (_lock)
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var locs = _fr.FaceLocations(img, numberOfTimesToUpsample: 0, model: _model).ToArray();
                    return locs.Select(l => new FaceBox
                    {
                        Left   = l.Left,
                        Top    = l.Top,
                        Width  = Math.Max(0, l.Right  - l.Left),
                        Height = Math.Max(0, l.Bottom - l.Top)
                    }).ToArray();
                }
            }
        }

        public double[] GetSingleFaceEncodingFromFile(string imagePath, out string error)
        {
            error = null;
            EnsureInit();

            lock (_lock)
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var locs = _fr.FaceLocations(img, numberOfTimesToUpsample: 0, model: _model).ToArray();
                    if (locs.Length == 0) { error = "NO_FACE";      return null; }
                    if (locs.Length > 1)  { error = "MULTI_FACE";   return null; }

                    var enc = _fr.FaceEncodings(img, new[] { locs[0] }).FirstOrDefault();
                    if (enc == null) { error = "ENCODING_FAIL"; return null; }

                    return enc.GetRawEncoding();
                }
            }
        }

        
        /// <summary>
        /// Detects exactly one face and returns both its bounding box and the underlying
        /// FaceRecognitionDotNet <see cref="Location"/>. Use this to avoid running
        /// FaceLocations twice in the kiosk pipeline.
        /// </summary>
        public bool TryDetectSingleFaceFromFile(
            string imagePath,
            out FaceBox faceBox,
            out Location faceLocation,
            out string error)
        {
            faceBox = null;
            faceLocation = default(Location);
            error = null;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "BAD_IMAGE_PATH";
                return false;
            }

            EnsureInit();

            lock (_lock)
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var locs = _fr.FaceLocations(img, numberOfTimesToUpsample: 0, model: _model).ToArray();
                    if (locs.Length == 0) { error = "NO_FACE"; return false; }
                    if (locs.Length > 1)  { error = "MULTI_FACE"; return false; }

                    var loc = locs[0];
                    faceLocation = loc;

                    faceBox = new FaceBox
                    {
                        Left   = loc.Left,
                        Top    = loc.Top,
                        Width  = Math.Max(0, loc.Right - loc.Left),
                        Height = Math.Max(0, loc.Bottom - loc.Top)
                    };

                    return true;
                }
            }
        }

        /// <summary>
        /// Encodes a face using a known <see cref="Location"/> (skips FaceLocations).
        /// Call this after liveness passes to avoid expensive work on spoof frames.
        /// </summary>
        public bool TryEncodeFromFileWithLocation(
            string imagePath,
            Location faceLocation,
            out double[] embedding,
            out string error)
        {
            embedding = null;
            error = null;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "BAD_IMAGE_PATH";
                return false;
            }

            EnsureInit();

            lock (_lock)
            {
                using (var img = FaceRecognition.LoadImageFile(imagePath))
                {
                    var enc = _fr.FaceEncodings(img, new[] { faceLocation }).FirstOrDefault();
                    if (enc == null) { error = "ENCODING_FAIL"; return false; }

                    embedding = enc.GetRawEncoding();
                    return true;
                }
            }
        }

// -------------------------------------------------------------------
        // Static helpers (unchanged)
        // -------------------------------------------------------------------

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
