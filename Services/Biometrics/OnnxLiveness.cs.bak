using System;
using System.Configuration;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Web;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceAttend.Services.Biometrics
{
    public sealed class OnnxLiveness : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly int _size;

        public OnnxLiveness()
        {
            var modelPath = ResolvePath(ConfigurationManager.AppSettings["Biometrics:LivenessModelPath"] ?? "~/App_Data/models/liveness/minifasnet.onnx");
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Liveness ONNX not found: " + modelPath);

            _session = new InferenceSession(modelPath);
            _size = int.TryParse(ConfigurationManager.AppSettings["Biometrics:LivenessInputSize"], out var s) ? s : 128;
        }

        public (bool Ok, float? Probability, string Error) ScoreFromFile(string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return (false, null, "NO_IMAGE");
            if (faceBox == null) return (false, null, "NO_FACE_BOX");

            using (var src = Image.FromFile(imagePath))
            using (var bmp = new Bitmap(src))
            using (var crop = CropFace(bmp, faceBox))
            {
                var p = Run(crop);
                if (!p.HasValue) return (false, null, "INFERENCE_FAILED");
                return (true, p.Value, null);
            }
        }

        // Returns probability of "real" (0..1)
        public float? Run(Bitmap faceRgb)
        {
            using (var resized = new Bitmap(_size, _size, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(resized))
            {
                g.DrawImage(faceRgb, 0, 0, _size, _size);

                // NCHW float32, RGB normalized to [0..1]
                var input = new DenseTensor<float>(new[] { 1, 3, _size, _size });

                for (int y = 0; y < _size; y++)
                {
                    for (int x = 0; x < _size; x++)
                    {
                        var c = resized.GetPixel(x, y);
                        input[0, 0, y, x] = c.R / 255f;
                        input[0, 1, y, x] = c.G / 255f;
                        input[0, 2, y, x] = c.B / 255f;
                    }
                }

                var inputName = _session.InputMetadata.Keys.First();

                // IMPORTANT: do NOT put the array in a using() (arrays are not IDisposable)
                var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, input) };

                try
                {
                    using (var results = _session.Run(inputs))
                    {
                        var r0 = results.First().AsEnumerable<float>().ToArray();

                        if (r0.Length == 1)
                            return Clamp01(r0[0]);

                        if (r0.Length >= 2)
                        {
                            // assume index 1 = "real"
                            var real = Softmax2(r0[0], r0[1]);
                            return Clamp01(real);
                        }

                        return null;
                    }
                }
                finally
                {
                    // Dispose inputs if they support it (depends on OnnxRuntime version)
                    foreach (var v in inputs)
                    {
                        if (v is IDisposable d) d.Dispose();
                    }
                }
            }
        }

        private static Bitmap CropFace(Bitmap bmp, DlibBiometrics.FaceBox b)
        {
            int pad = (int)(Math.Max(b.Width, b.Height) * 0.15);
            int x = Math.Max(0, b.Left - pad);
            int y = Math.Max(0, b.Top - pad);
            int w = Math.Min(bmp.Width - x, b.Width + pad * 2);
            int h = Math.Min(bmp.Height - y, b.Height + pad * 2);

            var rect = new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
            return bmp.Clone(rect, PixelFormat.Format24bppRgb);
        }

        private static float Softmax2(float a, float b)
        {
            var max = Math.Max(a, b);
            var ea = Math.Exp(a - max);
            var eb = Math.Exp(b - max);
            var sum = ea + eb;
            return (float)(eb / sum);
        }

        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private static string ResolvePath(string pathOrVirtual)
        {
            if (pathOrVirtual.StartsWith("~/"))
                return HttpContext.Current.Server.MapPath(pathOrVirtual);
            return pathOrVirtual;
        }
        public (bool Ok, float? Probability, string Error) ScoreFromImage(HttpPostedFileBase imageFile, DlibBiometrics.FaceBox faceBox)
        {
            if (imageFile == null) return (false, null, "NO_IMAGE");
            if (faceBox == null) return (false, null, "NO_FACE_BOX");

            // Make Image.FromStream safe by copying to memory
            using (var ms = new MemoryStream())
            {
                try { imageFile.InputStream.Position = 0; } catch { }
                imageFile.InputStream.CopyTo(ms);
                ms.Position = 0;

                using (var src = Image.FromStream(ms))
                using (var bmp = new Bitmap(src))
                using (var crop = CropFace(bmp, faceBox))
                {
                    var p = Run(crop);
                    if (!p.HasValue) return (false, null, "INFERENCE_FAILED");
                    return (true, p.Value, null);
                }
            }
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}

