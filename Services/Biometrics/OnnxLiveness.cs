using System;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceAttend.Services.Biometrics
{
    public sealed class OnnxLiveness : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;

        private readonly bool _isNhwc;
        private readonly int _inW;
        private readonly int _inH;

        private readonly float _cropScale;
        private readonly int _realIndex;
        private readonly string _outputType;
        private readonly string _normalize;

        private readonly int _cbFailStreak;
        private readonly int _cbDisableSeconds;
        private readonly int _slowMs;

        private readonly int _runTimeoutMs;       // inference timeout
        private readonly int _gateWaitMs;         // how long to wait for single-flight gate

        private static readonly object _cbLock = new object();
        private static int _failStreak = 0;
        private static DateTime _disabledUntilUtc = DateTime.MinValue;

        // Single-flight gate to prevent N concurrent ONNX calls
        private static readonly SemaphoreSlim _runGate = new SemaphoreSlim(1, 1);

        // If a timeout happens, we treat session as "unsafe" and keep gate locked forever
        // (prevents more hung threads). User must recycle IIS/app to recover.
        private static volatile bool _sessionStuck = false;

        public OnnxLiveness()
        {
            var modelPath = ResolvePath(ConfigurationManager.AppSettings["Biometrics:LivenessModelPath"]
                ?? "~/App_Data/models/liveness/minifasnet.onnx");

            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Liveness ONNX not found: " + modelPath);

            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();

            var dims = _session.InputMetadata[_inputName].Dimensions?.ToArray() ?? new int[0];
            var fallback = ParseInt(ConfigurationManager.AppSettings["Biometrics:LivenessInputSize"], 128);

            bool nhwc = false;
            int h = fallback, w = fallback;

            if (dims.Length == 4)
            {
                if (dims[1] == 3 && dims[2] > 0 && dims[3] > 0)
                {
                    nhwc = false; h = dims[2]; w = dims[3];
                }
                else if (dims[3] == 3 && dims[1] > 0 && dims[2] > 0)
                {
                    nhwc = true; h = dims[1]; w = dims[2];
                }
            }

            _isNhwc = nhwc;
            _inH = h;
            _inW = w;

            _cropScale = ParseFloat(ConfigurationManager.AppSettings["Biometrics:Liveness:CropScale"], 2.7f);
            _realIndex = ParseInt(ConfigurationManager.AppSettings["Biometrics:Liveness:RealIndex"], 1);
            _outputType = (ConfigurationManager.AppSettings["Biometrics:Liveness:OutputType"] ?? "logits").Trim().ToLowerInvariant();
            _normalize = (ConfigurationManager.AppSettings["Biometrics:Liveness:Normalize"] ?? "0_1").Trim().ToLowerInvariant();

            _cbFailStreak = ParseInt(ConfigurationManager.AppSettings["Biometrics:Liveness:CircuitFailStreak"], 3);
            _cbDisableSeconds = ParseInt(ConfigurationManager.AppSettings["Biometrics:Liveness:CircuitDisableSeconds"], 30);
            _slowMs = ParseInt(ConfigurationManager.AppSettings["Biometrics:Liveness:SlowMs"], 1200);

            _runTimeoutMs = ParseInt(ConfigurationManager.AppSettings["Biometrics:Liveness:RunTimeoutMs"], 1500);
            _gateWaitMs = ParseInt(ConfigurationManager.AppSettings["Biometrics:Liveness:GateWaitMs"], 300);
        }

        public (bool Ok, float? Probability, string Error) ScoreFromFile(string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return (false, null, "NO_IMAGE");
            if (faceBox == null) return (false, null, "NO_FACE_BOX");
            if (_sessionStuck) return (false, null, "LIVENESS_STUCK");
            if (IsDisabled()) return (false, null, "LIVENESS_DISABLED");

            try
            {
                using (var src = Image.FromFile(imagePath))
                using (var bmp = new Bitmap(src))
                using (var crop = CropFaceSquare(bmp, faceBox, _cropScale))
                {
                    return TryRun(crop);
                }
            }
            catch (Exception ex)
            {
                RegisterFail();
                return (false, null, "FILE_FAILED: " + ex.Message);
            }
        }

        private (bool Ok, float? Probability, string Error) TryRun(Bitmap faceRgb)
        {
            if (_sessionStuck) return (false, null, "LIVENESS_STUCK");
            if (IsDisabled()) return (false, null, "LIVENESS_DISABLED");

            // Gate wait: avoid stacking requests
            if (!_runGate.Wait(_gateWaitMs))
            {
                RegisterFail();
                return (false, null, "LIVENESS_BUSY");
            }

            var gateReleased = false;

            try
            {
                var sw = Stopwatch.StartNew();

                // Run inference on a worker thread so we can time out
                var task = Task.Run(() => RunInternal(faceRgb));

                if (!task.Wait(_runTimeoutMs))
                {
                    // We cannot safely cancel a hung ONNX call.
                    // Mark stuck so all future calls fail fast, and DO NOT release the gate.
                    _sessionStuck = true;
                    RegisterFail();
                    return (false, null, "LIVENESS_TIMEOUT");
                }

                var p = task.Result;

                sw.Stop();
                if (_slowMs > 0 && sw.ElapsedMilliseconds > _slowMs)
                {
                    RegisterFail();
                    return (false, null, "LIVENESS_SLOW");
                }

                if (!p.HasValue)
                {
                    RegisterFail();
                    return (false, null, "INFERENCE_FAILED");
                }

                RegisterSuccess();
                return (true, p.Value, null);
            }
            catch (Exception ex)
            {
                RegisterFail();
                return (false, null, "INFERENCE_ERROR: " + ex.Message);
            }
            finally
            {
                // Only release if not stuck (timeout means gate stays held to prevent more damage)
                if (!_sessionStuck)
                {
                    try { _runGate.Release(); gateReleased = true; } catch { }
                }

                // If we released, fine. If we didn't, liveness is effectively disabled until recycle.
                if (!gateReleased && _sessionStuck)
                {
                    // open circuit longer
                    lock (_cbLock)
                    {
                        _disabledUntilUtc = DateTime.UtcNow.AddDays(1);
                    }
                }
            }
        }

        // Returns probability of "real" (0..1)
        private float? RunInternal(Bitmap faceRgb)
        {
            using (var resized = ResizeToInput(faceRgb, _inW, _inH))
            {
                var input = BuildInputTensor(resized, _inW, _inH, _isNhwc);

                NamedOnnxValue[] inputs = null;
                try
                {
                    inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) };

                    using (var results = _session.Run(inputs))
                    {
                        var raw = results.First().AsTensor<float>().ToArray();
                        if (raw == null || raw.Length == 0) return null;

                        if (raw.Length == 1)
                        {
                            if (_outputType == "sigmoid") return Clamp01(Sigmoid(raw[0]));
                            return Clamp01(raw[0]);
                        }

                        float p0, p1;

                        if (_outputType == "prob")
                        {
                            p0 = Clamp01(raw[0]);
                            p1 = Clamp01(raw[1]);
                        }
                        else
                        {
                            p0 = Softmax2(raw[0], raw[1], 0);
                            p1 = Softmax2(raw[0], raw[1], 1);
                        }

                        return Clamp01((_realIndex <= 0) ? p0 : p1);
                    }
                }
                finally
                {
                    if (inputs != null)
                    {
                        foreach (var v in inputs)
                        {
                            try { (v as IDisposable)?.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        private DenseTensor<float> BuildInputTensor(Bitmap bmp, int w, int h, bool nhwc)
        {
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
                        int row = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int i = row + (x * 3);

                            byte b = buffer[i + 0];
                            byte g = buffer[i + 1];
                            byte r = buffer[i + 2];

                            float rf = Norm(r);
                            float gf = Norm(g);
                            float bf = Norm(b);

                            if (nhwc)
                            {
                                int o = (y * w * 3) + (x * 3);
                                outData[o + 0] = rf;
                                outData[o + 1] = gf;
                                outData[o + 2] = bf;
                            }
                            else
                            {
                                int hw = h * w;
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
            if (_normalize == "minus1_1") return (v / 127.5f) - 1f;
            if (_normalize == "centered") return (v - 127.5f) / 128f;
            return v / 255f;
        }

        private static Bitmap Ensure24bpp(Bitmap bmp)
        {
            if (bmp.PixelFormat == PixelFormat.Format24bppRgb)
                return (Bitmap)bmp.Clone();

            var clone = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(clone))
            {
                g.InterpolationMode = InterpolationMode.Low;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            }
            return clone;
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

        private static Bitmap CropFaceSquare(Bitmap bmp, DlibBiometrics.FaceBox box, float scale)
        {
            float cx = box.Left + (box.Width / 2f);
            float cy = box.Top + (box.Height / 2f);

            float side = Math.Max(box.Width, box.Height) * Math.Max(1f, scale);

            int left = (int)Math.Round(cx - (side / 2f));
            int top = (int)Math.Round(cy - (side / 2f));
            int w = (int)Math.Round(side);
            int h = (int)Math.Round(side);

            if (left < 0) { w += left; left = 0; }
            if (top < 0) { h += top; top = 0; }
            if (left + w > bmp.Width) w = bmp.Width - left;
            if (top + h > bmp.Height) h = bmp.Height - top;

            if (w < 8 || h < 8)
                return (Bitmap)bmp.Clone();

            var rect = new Rectangle(left, top, w, h);
            return bmp.Clone(rect, PixelFormat.Format24bppRgb);
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

        private static float Sigmoid(float x) => 1f / (1f + (float)Math.Exp(-x));
        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        private bool IsDisabled()
        {
            lock (_cbLock) { return DateTime.UtcNow < _disabledUntilUtc; }
        }

        private void RegisterSuccess()
        {
            lock (_cbLock) { _failStreak = 0; }
        }

        private void RegisterFail()
        {
            if (_cbFailStreak <= 0 || _cbDisableSeconds <= 0) return;

            lock (_cbLock)
            {
                _failStreak++;
                if (_failStreak >= _cbFailStreak)
                {
                    _disabledUntilUtc = DateTime.UtcNow.AddSeconds(_cbDisableSeconds);
                    _failStreak = 0;
                }
            }
        }

        private static int ParseInt(string s, int fallback)
        {
            if (int.TryParse((s ?? "").Trim(), out var v)) return v;
            return fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (float.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
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
