using System;
using System.Configuration;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceAttend.Services.Biometrics
{
    public sealed class OnnxLiveness : IDisposable
    {
        public sealed class Score
        {
            public bool Ok { get; set; }
            public float? Probability { get; set; }   // "real" probability (based on config)
            public string Error { get; set; }

            // debug helpers
            public float[] Raw { get; set; }          // raw model output (logits or probs)
            public float? P0 { get; set; }            // softmax/prob class 0
            public float? P1 { get; set; }            // softmax/prob class 1
            public string Note { get; set; }          // layout/shape/normalize info
        }

        private readonly InferenceSession _session;
        private readonly string _inputName;

        private readonly bool _isNhwc;
        private readonly int _inW;
        private readonly int _inH;

        private readonly float _cropScale;
        private readonly int _realIndex;              // which class index means "real"
        private readonly string _outputType;          // logits | prob | sigmoid
        private readonly string _norm;                // 0_1 | minus1_1 | centered
        private readonly bool _debug;

        public OnnxLiveness()
        {
            var modelPath = ResolvePath(ConfigurationManager.AppSettings["Biometrics:LivenessModelPath"]
                ?? "~/App_Data/models/liveness/minifasnet.onnx");

            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Liveness ONNX not found: " + modelPath);

            _session = new InferenceSession(modelPath);

            _inputName = _session.InputMetadata.Keys.First();
            var dims = _session.InputMetadata[_inputName].Dimensions?.ToArray() ?? new int[0];

            // defaults if model has dynamic dims
            var cfgSize = ParseInt(ConfigurationManager.AppSettings["Biometrics:LivenessInputSize"], 128);
            _inW = cfgSize;
            _inH = cfgSize;
            _isNhwc = false;

            // Try infer layout + size
            // NCHW: [1,3,H,W]
            // NHWC: [1,H,W,3]
            if (dims.Length == 4)
            {
                if (dims[1] == 3 && dims[2] > 0 && dims[3] > 0)
                {
                    _isNhwc = false;
                    _inH = dims[2];
                    _inW = dims[3];
                }
                else if (dims[3] == 3 && dims[1] > 0 && dims[2] > 0)
                {
                    _isNhwc = true;
                    _inH = dims[1];
                    _inW = dims[2];
                }
            }

            _cropScale = ParseFloat(ConfigurationManager.AppSettings["Biometrics:Liveness:CropScale"], 2.7f);
            _realIndex = ParseInt(ConfigurationManager.AppSettings["Biometrics:Liveness:RealIndex"], 1);

            _outputType = (ConfigurationManager.AppSettings["Biometrics:Liveness:OutputType"] ?? "logits")
                .Trim().ToLowerInvariant();

            _norm = (ConfigurationManager.AppSettings["Biometrics:Liveness:Normalize"] ?? "0_1")
                .Trim().ToLowerInvariant();

            _debug = string.Equals(ConfigurationManager.AppSettings["Biometrics:Debug"], "true", StringComparison.OrdinalIgnoreCase);
        }

        public Score ScoreFromImage(HttpPostedFileBase imageFile, DlibBiometrics.FaceBox box)
        {
            if (imageFile == null) return new Score { Ok = false, Error = "NO_IMAGE" };
            if (box == null) return new Score { Ok = false, Error = "NO_FACE_BOX" };

            try
            {
                using (var ms = new MemoryStream())
                {
                    imageFile.InputStream.CopyTo(ms);
                    ms.Position = 0;

                    using (var bmp = new Bitmap(ms))
                    {
                        using (var crop = CropFace(bmp, box, _cropScale))
                        using (var resized = ResizeToInput(crop, _inW, _inH))
                        {
                            var input = BuildInputTensor(resized, _inW, _inH, _isNhwc);

                            using (var inputs = new DisposableNamedOnnxValue[]
                            {
                                DisposableNamedOnnxValue.CreateFromTensor(_inputName, input)
                            })
                            {
                                using (var results = _session.Run(inputs))
                                {
                                    var raw = results.First().AsTensor<float>().ToArray();
                                    if (raw == null || raw.Length == 0)
                                        return new Score { Ok = false, Error = "EMPTY_OUTPUT" };

                                    float? p0 = null;
                                    float? p1 = null;
                                    float realP;

                                    if (_outputType == "sigmoid")
                                    {
                                        // single logit -> sigmoid
                                        var v = raw[0];
                                        realP = Sigmoid(v);
                                    }
                                    else if (raw.Length >= 2)
                                    {
                                        if (_outputType == "prob")
                                        {
                                            p0 = Clamp01(raw[0]);
                                            p1 = Clamp01(raw[1]);
                                        }
                                        else
                                        {
                                            // logits -> softmax
                                            var sp0 = Softmax2(raw[0], raw[1], 0);
                                            var sp1 = Softmax2(raw[0], raw[1], 1);
                                            p0 = Clamp01(sp0);
                                            p1 = Clamp01(sp1);
                                        }

                                        var idx = (_realIndex <= 0) ? 0 : 1;
                                        realP = Clamp01(idx == 0 ? (p0 ?? 0f) : (p1 ?? 0f));
                                    }
                                    else
                                    {
                                        // raw.Length == 1 and not sigmoid => treat as probability already
                                        realP = Clamp01(raw[0]);
                                    }

                                    return new Score
                                    {
                                        Ok = true,
                                        Probability = Clamp01(realP),
                                        Raw = _debug ? raw : null,
                                        P0 = _debug ? p0 : null,
                                        P1 = _debug ? p1 : null,
                                        Note = _debug ? $"layout={(_isNhwc ? "NHWC" : "NCHW")} size={_inW}x{_inH} norm={_norm} cropScale={_cropScale} out={_outputType} realIndex={_realIndex}" : null
                                    };
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new Score { Ok = false, Error = "INFERENCE_FAILED: " + ex.Message };
            }
        }

        private DenseTensor<float> BuildInputTensor(Bitmap bmp, int w, int h, bool nhwc)
        {
            // Force 24bpp for predictable BGR byte layout
            using (var src = Ensure24bpp(bmp))
            {
                var rect = new Rectangle(0, 0, w, h);
                var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var stride = data.Stride;
                    var bytes = Math.Abs(stride) * h;
                    var buffer = new byte[bytes];
                    Marshal.Copy(data.Scan0, buffer, 0, bytes);

                    var outData = new float[w * h * 3];

                    for (int y = 0; y < h; y++)
                    {
                        var row = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            var i = row + (x * 3);

                            // Format24bppRgb stores bytes as B,G,R
                            var b = buffer[i + 0];
                            var g = buffer[i + 1];
                            var r = buffer[i + 2];

                            var rf = Norm(r);
                            var gf = Norm(g);
                            var bf = Norm(b);

                            if (nhwc)
                            {
                                var o = (y * w * 3) + (x * 3);
                                outData[o + 0] = rf;
                                outData[o + 1] = gf;
                                outData[o + 2] = bf;
                            }
                            else
                            {
                                // NCHW: c*H*W + y*W + x
                                var hw = h * w;
                                outData[(0 * hw) + (y * w) + x] = rf;
                                outData[(1 * hw) + (y * w) + x] = gf;
                                outData[(2 * hw) + (y * w) + x] = bf;
                            }
                        }
                    }

                    var dims = nhwc ? new[] { 1, h, w, 3 } : new[] { 1, 3, h, w };
                    return new DenseTensor<float>(outData, dims);
                }
                finally
                {
                    src.UnlockBits(data);
                }
            }
        }

        private float Norm(byte v)
        {
            if (_norm == "minus1_1")
                return (v / 127.5f) - 1f;

            if (_norm == "centered")
                return (v - 127.5f) / 128f;

            // default 0..1
            return v / 255f;
        }

        private static Bitmap Ensure24bpp(Bitmap bmp)
        {
            if (bmp.PixelFormat == PixelFormat.Format24bppRgb)
                return (Bitmap)bmp.Clone();

            var clone = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(clone))
            {
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.Low;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            }
            return clone;
        }

        private static Bitmap CropFace(Bitmap bmp, DlibBiometrics.FaceBox box, float scale)
        {
            // square crop around center, scaled up
            var cx = box.Left + (box.Width / 2f);
            var cy = box.Top + (box.Height / 2f);
            var side = Math.Max(box.Width, box.Height) * Math.Max(1.0f, scale);

            var left = (int)Math.Round(cx - (side / 2f));
            var top = (int)Math.Round(cy - (side / 2f));
            var w = (int)Math.Round(side);
            var h = (int)Math.Round(side);

            // clamp
            if (left < 0) { w += left; left = 0; }
            if (top < 0) { h += top; top = 0; }
            if (left + w > bmp.Width) w = bmp.Width - left;
            if (top + h > bmp.Height) h = bmp.Height - top;

            if (w <= 10 || h <= 10)
                return (Bitmap)bmp.Clone();

            var rect = new Rectangle(left, top, w, h);
            return bmp.Clone(rect, bmp.PixelFormat);
        }

        private static Bitmap ResizeToInput(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        private static float Softmax2(float a, float b, int index)
        {
            var m = Math.Max(a, b);
            var ea = (float)Math.Exp(a - m);
            var eb = (float)Math.Exp(b - m);
            var sum = ea + eb;
            if (sum <= 0f) return 0.5f;
            return index == 0 ? (ea / sum) : (eb / sum);
        }

        private static float Sigmoid(float x)
        {
            return 1f / (1f + (float)Math.Exp(-x));
        }

        private static float Clamp01(float x)
        {
            if (x < 0f) return 0f;
            if (x > 1f) return 1f;
            return x;
        }

        private static int ParseInt(string s, int fallback)
        {
            if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (float.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;
            return fallback;
        }

        private static string ResolvePath(string pathOrVirtual)
        {
            if (pathOrVirtual.StartsWith("~/"))
                return HttpContext.Current.Server.MapPath(pathOrVirtual);
            return pathOrVirtual;
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}
