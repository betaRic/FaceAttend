using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using FaceRecognitionDotNet;

namespace FaceAttend.Services.Biometrics
{
    public sealed class DlibBiometrics : IDisposable
    {
        public sealed class FaceBox
        {
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private readonly FaceRecognition _fr;
        private readonly string _tmpDir;
        private readonly int _maxBytes;

        public DlibBiometrics()
        {
            var modelsDir = ResolvePath(ConfigurationManager.AppSettings["Biometrics:DlibModelsDir"] ?? "~/App_Data/models/dlib");
            if (!Directory.Exists(modelsDir))
                throw new DirectoryNotFoundException("Dlib models folder not found: " + modelsDir);

            FaceRecognition.InternalEncoding = Encoding.UTF8;
            _fr = FaceRecognition.Create(modelsDir);

            _tmpDir = ResolvePath("~/App_Data/_tmp");
            Directory.CreateDirectory(_tmpDir);

            if (int.TryParse(ConfigurationManager.AppSettings["Biometrics:MaxUploadBytes"], out var b) && b > 0)
                _maxBytes = b;
            else
                _maxBytes = 10 * 1024 * 1024;
        }

        public FaceBox[] DetectFaces(HttpPostedFileBase imageFile)
        {
            if (imageFile == null) return new FaceBox[0];

            var path = SaveToTemp(imageFile);
            try
            {
                return DetectFacesFromFile(path);
            }
            finally
            {
                SafeDelete(path);
            }
        }

        public FaceBox[] DetectFacesFromFile(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return new FaceBox[0];

            using (var img = FaceRecognition.LoadImageFile(imagePath))
            {
                var model = ReadDetectorModel();
                var locs = _fr.FaceLocations(img, numberOfTimesToUpsample: 1, model: model).ToArray();
                return locs.Select(ToBox).ToArray();
            }
        }

        // Returns 128D encoding for exactly 1 face; otherwise null.
        public double[] GetSingleFaceEncoding(HttpPostedFileBase imageFile, out string error)
        {
            error = null;
            if (imageFile == null) { error = "NO_IMAGE"; return null; }

            var path = SaveToTemp(imageFile);
            try
            {
                return GetSingleFaceEncodingFromFile(path, out error);
            }
            finally
            {
                SafeDelete(path);
            }
        }

        public double[] GetSingleFaceEncodingFromFile(string imagePath, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                error = "NO_IMAGE";
                return null;
            }

            using (var img = FaceRecognition.LoadImageFile(imagePath))
            {
                var model = ReadDetectorModel();
                var locs = _fr.FaceLocations(img, numberOfTimesToUpsample: 1, model: model).ToArray();

                if (locs.Length == 0) { error = "NO_FACE"; return null; }
                if (locs.Length > 1) { error = "MULTIPLE_FACES"; return null; }

                var enc = _fr.FaceEncodings(img, knownFaceLocation: locs).FirstOrDefault();
                if (enc == null) { error = "ENCODING_FAILED"; return null; }

                return enc.GetRawEncoding();
            }
        }

        public static double Distance(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return double.PositiveInfinity;
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var d = a[i] - b[i];
                sum += d * d;
            }
            return Math.Sqrt(sum);
        }

        public static byte[] EncodeToBytes(double[] v)
        {
            if (v == null) return null;
            var bytes = new byte[v.Length * sizeof(double)];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static double[] DecodeFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length % sizeof(double) != 0) return null;
            var v = new double[bytes.Length / sizeof(double)];
            Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
            return v;
        }

        private static Model ReadDetectorModel()
        {
            var s = (ConfigurationManager.AppSettings["Biometrics:DlibDetector"] ?? "hog").Trim().ToLowerInvariant();
            return s == "cnn" ? Model.Cnn : Model.Hog;
        }

        private static FaceBox ToBox(Location loc)
        {
            return new FaceBox
            {
                Left = loc.Left,
                Top = loc.Top,
                Width = loc.Right - loc.Left,
                Height = loc.Bottom - loc.Top
            };
        }

        private string SaveToTemp(HttpPostedFileBase file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));

            if (file.ContentLength <= 0)
                throw new InvalidDataException("EMPTY_IMAGE");

            if (file.ContentLength > _maxBytes)
                throw new InvalidDataException("IMAGE_TOO_LARGE");

            var ext = (Path.GetExtension(file.FileName) ?? "").Trim().ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") ext = ".jpg";

            var path = Path.Combine(_tmpDir, Guid.NewGuid().ToString("N") + ext);
            file.SaveAs(path);
            return path;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private static string ResolvePath(string pathOrVirtual)
        {
            if (pathOrVirtual.StartsWith("~/"))
                return HttpContext.Current.Server.MapPath(pathOrVirtual);
            return pathOrVirtual;
        }

        public void Dispose()
        {
            _fr?.Dispose();
        }
    }
}
