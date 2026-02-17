using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;
using FaceAttend.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceAttend.Services.Biometrics
{
    public class OnnxLiveness
    {
        private static readonly object _lock = new object();
        private static InferenceSession _session;
        private static string _inputName;
        private static string _outputName;

        private static int _failStreak = 0;
        private static DateTime _circuitUntilUtc = DateTime.MinValue;
        private static bool _stuck = false;

        public (bool Ok, float? Probability, string Error) ScoreFromFile(string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                if (_stuck) return (false, null, "SESSION_STUCK");
                if (now < _circuitUntilUtc) return (false, null, "CIRCUIT_OPEN");
                EnsureSession();
            }

            int inputSize = SystemConfigService.GetIntCached("Biometrics:LivenessInputSize", AppSettings.GetInt("Biometrics:LivenessInputSize", 128));
            double cropScale = SystemConfigService.GetDoubleCached("Biometrics:Liveness:CropScale", AppSettings.GetDouble("Biometrics:Liveness:CropScale", 2.7));
            int realIndex = SystemConfigService.GetIntCached("Biometrics:Liveness:RealIndex", AppSettings.GetInt("Biometrics:Liveness:RealIndex", 1));
            string outputType = SystemConfigService.GetStringCached("Biometrics:Liveness:OutputType", AppSettings.GetString("Biometrics:Liveness:OutputType", "logits"));
            string normalize = SystemConfigService.GetStringCached("Biometrics:Liveness:Normalize", AppSettings.GetString("Biometrics:Liveness:Normalize", "0_1"));
            string chanOrder = SystemConfigService.GetStringCached("Biometrics:Liveness:ChannelOrder", AppSettings.GetString("Biometrics:Liveness:ChannelOrder", "RGB"));

            // Optional: take the best (or average) over multiple crop scales.
            // This lets you tune liveness behavior without changing the threshold.
            var decision = SystemConfigService.GetStringCached("Biometrics:Liveness:Decision", AppSettings.GetString("Biometrics:Liveness:Decision", "max"));
            var multiCsv = SystemConfigService.GetStringCached("Biometrics:Liveness:MultiCropScales", AppSettings.GetString("Biometrics:Liveness:MultiCropScales", ""));
            var scales = ParseScales(multiCsv);
            if (scales.Count == 0) scales.Add(cropScale);

            int slowMs = SystemConfigService.GetIntCached("Biometrics:Liveness:SlowMs", AppSettings.GetInt("Biometrics:Liveness:SlowMs", 1200));
            int timeoutMs = SystemConfigService.GetIntCached("Biometrics:Liveness:RunTimeoutMs", AppSettings.GetInt("Biometrics:Liveness:RunTimeoutMs", 1500));
            int gateWaitMs = SystemConfigService.GetIntCached("Biometrics:Liveness:GateWaitMs", AppSettings.GetInt("Biometrics:Liveness:GateWaitMs", 300));

            var sw = Stopwatch.StartNew();
            try
            {
                // gate (single flight)
                if (!Monitor.TryEnter(_lock, gateWaitMs))
                    return Fail("GATE_BUSY", null);

                try
                {
                    if (_stuck) return (false, null, "SESSION_STUCK");

                    var probs = new List<float>();

                    foreach (var s in scales)
                    {
                        var tensor = BuildTensor(imagePath, faceBox, inputSize, s, normalize, chanOrder);
                        if (tensor == null) return Fail("PREPROCESS_FAIL", null);

                        var inputs = new List<NamedOnnxValue>
                        {
                            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                        };

                        // run with timeout wrapper
                        var task = Task.Run(() =>
                        {
                            using (var results = _session.Run(inputs))
                            {
                                var outTensor = results.First(x => x.Name == _outputName).AsTensor<float>();
                                return outTensor.ToArray();
                            }
                        });

                        if (!task.Wait(timeoutMs))
                        {
                            lock (_lock) { _stuck = true; }
                            return Fail("TIMEOUT", null);
                        }

                        var raw = task.Result; // logits or probs
                        if (raw == null || raw.Length < 2) return Fail("BAD_OUTPUT", null);

                        float p;
                        if (outputType.Equals("probs", StringComparison.OrdinalIgnoreCase))
                        {
                            p = raw[Math.Max(0, Math.Min(realIndex, raw.Length - 1))];
                        }
                        else
                        {
                            p = Softmax(raw)[realIndex];
                        }

                        probs.Add(p);
                    }

                    float finalP;
                    if (decision.Equals("avg", StringComparison.OrdinalIgnoreCase))
                        finalP = probs.Count == 0 ? 0f : probs.Average();
                    else
                        finalP = probs.Count == 0 ? 0f : probs.Max();

                    lock (_lock) { _failStreak = 0; }
                    if (sw.ElapsedMilliseconds >= slowMs) { /* optional: log */ }

                    return (true, finalP, null);
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
            catch (Exception ex)
            {
                return Fail("ONNX_ERROR: " + ex.Message, null);
            }
        }

        private static void EnsureSession()
        {
            if (_session != null) return;

            var modelRel = AppSettings.GetString("Biometrics:LivenessModelPath", "~/App_Data/models/liveness/minifasnet.onnx");
            var modelPath = HostingEnvironment.MapPath(modelRel);
            if (string.IsNullOrWhiteSpace(modelPath) || !System.IO.File.Exists(modelPath))
                throw new InvalidOperationException("Liveness model not found: " + modelRel);

            var opts = new SessionOptions();
            _session = new InferenceSession(modelPath, opts);

            _inputName = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();
        }

        private static DenseTensor<float> BuildTensor(string imagePath, DlibBiometrics.FaceBox faceBox, int inputSize, double cropScale, string normalize, string chanOrder)
        {
            using (var src = new Bitmap(imagePath))
            {
                // Expand around face center
                double cx = faceBox.Left + faceBox.Width / 2.0;
                double cy = faceBox.Top + faceBox.Height / 2.0;
                double w = faceBox.Width * cropScale;
                double h = faceBox.Height * cropScale;

                int left = (int)Math.Round(cx - w / 2.0);
                int top = (int)Math.Round(cy - h / 2.0);
                int right = (int)Math.Round(cx + w / 2.0);
                int bottom = (int)Math.Round(cy + h / 2.0);

                left = Math.Max(0, left);
                top = Math.Max(0, top);
                right = Math.Min(src.Width, right);
                bottom = Math.Min(src.Height, bottom);

                int cw = Math.Max(1, right - left);
                int ch = Math.Max(1, bottom - top);

                using (var cropped = new Bitmap(cw, ch))
                using (var g = Graphics.FromImage(cropped))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, cw, ch), new Rectangle(left, top, cw, ch), GraphicsUnit.Pixel);

                    using (var resized = new Bitmap(inputSize, inputSize, PixelFormat.Format24bppRgb))
                    using (var gr = Graphics.FromImage(resized))
                    {
                        gr.CompositingQuality = CompositingQuality.HighQuality;
                        gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        gr.SmoothingMode = SmoothingMode.HighQuality;
                        gr.DrawImage(cropped, new Rectangle(0, 0, inputSize, inputSize));

                        return BuildTensorFromBitmap(resized, normalize, chanOrder);
                    }
                }
            }
        }

        // Faster than GetPixel(): uses LockBits + Marshal.Copy.
        private static DenseTensor<float> BuildTensorFromBitmap(Bitmap bmp, string normalize, string chanOrder)
        {
            int w = bmp.Width;
            int h = bmp.Height;

            // NCHW: [1,3,H,W]
            var t = new DenseTensor<float>(new[] { 1, 3, h, w });

            var rect = new Rectangle(0, 0, w, h);
            BitmapData data = null;
            try
            {
                data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int stride = data.Stride;
                int bytes = stride * h;

                var buffer = new byte[bytes];
                Marshal.Copy(data.Scan0, buffer, 0, bytes);

                bool wantBgr = (chanOrder ?? "RGB").Trim().Equals("BGR", StringComparison.OrdinalIgnoreCase);

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + (x * 3);

                        // Format24bppRgb = B,G,R byte order
                        float b = buffer[i + 0];
                        float g = buffer[i + 1];
                        float r = buffer[i + 2];

                        float c0, c1, c2;
                        if (wantBgr)
                        {
                            c0 = b; c1 = g; c2 = r;
                        }
                        else
                        {
                            c0 = r; c1 = g; c2 = b;
                        }

                        Normalize(ref c0, ref c1, ref c2, normalize);

                        t[0, 0, y, x] = c0;
                        t[0, 1, y, x] = c1;
                        t[0, 2, y, x] = c2;
                    }
                }

                return t;
            }
            finally
            {
                if (data != null)
                {
                    try { bmp.UnlockBits(data); } catch { }
                }
            }
        }

        private static void Normalize(ref float c0, ref float c1, ref float c2, string mode)
        {
            // Most models accept 0..1.
            if (mode == null) mode = "0_1";
            mode = mode.Trim().ToLowerInvariant();

            if (mode == "none")
            {
                // keep 0..255
                return;
            }

            if (mode == "minus1_1")
            {
                c0 = (c0 / 127.5f) - 1f;
                c1 = (c1 / 127.5f) - 1f;
                c2 = (c2 / 127.5f) - 1f;
                return;
            }

            if (mode == "imagenet")
            {
                // (x/255 - mean) / std
                c0 = (c0 / 255f - 0.485f) / 0.229f;
                c1 = (c1 / 255f - 0.456f) / 0.224f;
                c2 = (c2 / 255f - 0.406f) / 0.225f;
                return;
            }

            // default: 0..1
            c0 = c0 / 255f;
            c1 = c1 / 255f;
            c2 = c2 / 255f;
        }

        private static List<double> ParseScales(string csv)
        {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(csv)) return list;

            var parts = csv.Split(',');
            foreach (var p in parts)
            {
                if (double.TryParse((p ?? "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                {
                    if (d > 0.5 && d < 10) list.Add(d);
                }
            }
            return list;
        }

        private static float[] Softmax(float[] logits)
        {
            float max = logits.Max();
            var exps = logits.Select(x => (float)Math.Exp(x - max)).ToArray();
            float sum = exps.Sum();
            if (sum <= 0) return logits.Select(_ => 0f).ToArray();
            for (int i = 0; i < exps.Length; i++) exps[i] /= sum;
            return exps;
        }

        private static (bool Ok, float? Probability, string Error) Fail(string error, float? p)
        {
            lock (_lock)
            {
                _failStreak++;
                int streak = AppSettings.GetInt("Biometrics:Liveness:CircuitFailStreak", 3);
                int seconds = AppSettings.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30);
                if (_failStreak >= streak)
                {
                    _circuitUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
                    _failStreak = 0;
                }
            }
            return (false, p, error);
        }
    }
}
