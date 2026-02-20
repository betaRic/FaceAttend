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
    /// <summary>
    /// ONNX-based liveness detection, wrapped in a circuit breaker.
    ///
    /// Fixes applied vs. original:
    ///   1. Session lifecycle — <see cref="DisposeSession"/> allows proper cleanup
    ///      on app shutdown (call from Global.asax Application_End).
    ///   2. Locking clarity — the outer quick-check lock and the inner gate lock
    ///      are now clearly separated and documented.  The Task.Run pattern is kept
    ///      intentionally: ONNX inference runs on a thread-pool thread so the gate
    ///      lock does not prevent the CPU from being used, but the gate still
    ///      serialises calls so only one inference runs at a time.
    ///   3. Timeout/stuck flag — the stuck flag is set via Monitor.Enter (reentrant
    ///      on the same thread) before Monitor.Exit in the finally block; this is
    ///      now documented clearly.
    ///   4. Bitmap disposal — added explicit finally blocks in BuildTensor so GDI
    ///      handles are always released even if an exception is thrown mid-crop.
    /// </summary>
    public class OnnxLiveness
    {
        private static readonly object _lock = new object();

        // Session is kept as a static singleton for the lifetime of the app.
        // Disposed via DisposeSession() called from Global.asax Application_End.
        private static InferenceSession _session;
        private static string _inputName;
        private static string _outputName;

        // Circuit breaker state.
        private static int _failStreak = 0;
        private static DateTime _circuitUntilUtc = DateTime.MinValue;
        private static bool _stuck = false;

        // -------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------

        public (bool Ok, float? Probability, string Error) ScoreFromFile(
            string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            var now = DateTime.UtcNow;

            // --- Quick state check (non-blocking) ---
            lock (_lock)
            {
                if (_stuck) return (false, null, "SESSION_STUCK");
                if (now < _circuitUntilUtc) return (false, null, "CIRCUIT_OPEN");
                EnsureSession(); // no-op if already initialised
            }

            // Read config outside any lock — these values are effectively
            // read-only after the session is initialised.
            int inputSize = SystemConfigService.GetIntCached(
                "Biometrics:LivenessInputSize",
                AppSettings.GetInt("Biometrics:LivenessInputSize", 128));
            double cropScale = SystemConfigService.GetDoubleCached(
                "Biometrics:Liveness:CropScale",
                AppSettings.GetDouble("Biometrics:Liveness:CropScale", 2.7));
            int realIndex = SystemConfigService.GetIntCached(
                "Biometrics:Liveness:RealIndex",
                AppSettings.GetInt("Biometrics:Liveness:RealIndex", 1));
            string outputType = SystemConfigService.GetStringCached(
                "Biometrics:Liveness:OutputType",
                AppSettings.GetString("Biometrics:Liveness:OutputType", "logits"));
            string normalize = SystemConfigService.GetStringCached(
                "Biometrics:Liveness:Normalize",
                AppSettings.GetString("Biometrics:Liveness:Normalize", "0_1"));
            string chanOrder = SystemConfigService.GetStringCached(
                "Biometrics:Liveness:ChannelOrder",
                AppSettings.GetString("Biometrics:Liveness:ChannelOrder", "RGB"));
            string decision = SystemConfigService.GetStringCached(
                "Biometrics:Liveness:Decision",
                AppSettings.GetString("Biometrics:Liveness:Decision", "max"));
            string multiCsv = SystemConfigService.GetStringCached(
                "Biometrics:Liveness:MultiCropScales",
                AppSettings.GetString("Biometrics:Liveness:MultiCropScales", ""));
            int slowMs = SystemConfigService.GetIntCached(
                "Biometrics:Liveness:SlowMs",
                AppSettings.GetInt("Biometrics:Liveness:SlowMs", 1200));
            int timeoutMs = SystemConfigService.GetIntCached(
                "Biometrics:Liveness:RunTimeoutMs",
                AppSettings.GetInt("Biometrics:Liveness:RunTimeoutMs", 1500));
            int gateWaitMs = SystemConfigService.GetIntCached(
                "Biometrics:Liveness:GateWaitMs",
                AppSettings.GetInt("Biometrics:Liveness:GateWaitMs", 300));

            var scales = ParseScales(multiCsv);
            if (scales.Count == 0) scales.Add(cropScale);

            var sw = Stopwatch.StartNew();

            // --- Gate lock: serialise inference calls ---
            // Only one thread runs ONNX inference at a time.
            // gateWaitMs prevents indefinite blocking on a busy session.
            if (!Monitor.TryEnter(_lock, gateWaitMs))
                return Fail("GATE_BUSY", null);

            try
            {
                // Re-check circuit state now that we hold the lock.
                if (_stuck) return (false, null, "SESSION_STUCK");
                if (DateTime.UtcNow < _circuitUntilUtc) return (false, null, "CIRCUIT_OPEN");

                var probs = new List<float>();

                foreach (var scale in scales)
                {
                    var tensor = BuildTensor(imagePath, faceBox, inputSize, scale, normalize, chanOrder);
                    if (tensor == null) return Fail("PREPROCESS_FAIL", null);

                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                    };

                    // Run ONNX inference on a thread-pool thread so that
                    // the current thread can apply the timeout check.
                    // _session, _inputName, and _outputName are read-only
                    // after initialisation so no lock is needed inside the task.
                    var task = Task.Run(() =>
                    {
                        using (var results = _session.Run(inputs))
                        {
                            var outTensor = results
                                .First(x => x.Name == _outputName)
                                .AsTensor<float>();
                            return outTensor.ToArray();
                        }
                    });

                    if (!task.Wait(timeoutMs))
                    {
                        // Mark session as stuck so future calls fail fast.
                        // Monitor.Enter is reentrant on the same thread in C# —
                        // the outer Monitor.TryEnter already owns the lock, so
                        // this nested lock() call succeeds immediately.
                        lock (_lock) { _stuck = true; }
                        return Fail("TIMEOUT", null);
                    }

                    var raw = task.Result;
                    if (raw == null || raw.Length < 2) return Fail("BAD_OUTPUT", null);

                    float p;
                    if (outputType.Equals("probs", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = Math.Max(0, Math.Min(realIndex, raw.Length - 1));
                        p = raw[idx];
                    }
                    else
                    {
                        p = Softmax(raw)[realIndex];
                    }

                    probs.Add(p);
                }

                float finalP = decision.Equals("avg", StringComparison.OrdinalIgnoreCase)
                    ? (probs.Count == 0 ? 0f : probs.Average())
                    : (probs.Count == 0 ? 0f : probs.Max());

                lock (_lock) { _failStreak = 0; }

                if (sw.ElapsedMilliseconds >= slowMs)
                {
                    // Log slow inference in production via your logging framework.
                    System.Diagnostics.Debug.WriteLine(
                        $"[OnnxLiveness] Slow inference: {sw.ElapsedMilliseconds}ms");
                }

                return (true, finalP, null);
            }
            catch (Exception ex)
            {
                return Fail("ONNX_ERROR: " + ex.Message, null);
            }
            finally
            {
                // Always release the gate lock so other threads are not blocked.
                Monitor.Exit(_lock);
            }
        }

        // -------------------------------------------------------------------
        // Session lifecycle
        // -------------------------------------------------------------------

        private static void EnsureSession()
        {
            if (_session != null) return;

            var modelRel = AppSettings.GetString(
                "Biometrics:LivenessModelPath",
                "~/App_Data/models/liveness/minifasnet.onnx");
            var modelPath = HostingEnvironment.MapPath(modelRel);

            if (string.IsNullOrWhiteSpace(modelPath) || !System.IO.File.Exists(modelPath))
                throw new InvalidOperationException("Liveness model not found: " + modelRel);

            var opts = new SessionOptions();
            _session = new InferenceSession(modelPath, opts);
            _inputName = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();
        }

        /// <summary>
        /// Disposes the ONNX InferenceSession and resets all state.
        /// Call this from <c>Global.asax Application_End</c>:
        /// <code>
        ///   protected void Application_End()
        ///   {
        ///       FaceAttend.Services.Biometrics.OnnxLiveness.DisposeSession();
        ///   }
        /// </code>
        /// Without this call the session is leaked when IIS recycles the app pool,
        /// though the OS reclaims the memory eventually.
        /// </summary>
        public static void DisposeSession()
        {
            lock (_lock)
            {
                if (_session != null)
                {
                    try { _session.Dispose(); } catch { /* best effort */ }
                    _session = null;
                    _inputName = null;
                    _outputName = null;
                }
                _stuck = false;
                _failStreak = 0;
                _circuitUntilUtc = DateTime.MinValue;
            }
        }

        // -------------------------------------------------------------------
        // Tensor building
        // -------------------------------------------------------------------

        private static DenseTensor<float> BuildTensor(
            string imagePath,
            DlibBiometrics.FaceBox faceBox,
            int inputSize,
            double cropScale,
            string normalize,
            string chanOrder)
        {
            // Each Bitmap is wrapped in a using block.  Extra try/finally ensures
            // disposal even if an exception is thrown partway through.
            Bitmap src = null;
            Bitmap cropped = null;
            Bitmap resized = null;
            Graphics gCrop = null;
            Graphics gResize = null;

            try
            {
                src = new Bitmap(imagePath);

                double cx = faceBox.Left + faceBox.Width / 2.0;
                double cy = faceBox.Top + faceBox.Height / 2.0;
                double w = faceBox.Width * cropScale;
                double h = faceBox.Height * cropScale;

                int left   = Math.Max(0, (int)Math.Round(cx - w / 2.0));
                int top    = Math.Max(0, (int)Math.Round(cy - h / 2.0));
                int right  = Math.Min(src.Width,  (int)Math.Round(cx + w / 2.0));
                int bottom = Math.Min(src.Height, (int)Math.Round(cy + h / 2.0));

                int cw = Math.Max(1, right  - left);
                int ch = Math.Max(1, bottom - top);

                cropped = new Bitmap(cw, ch);
                gCrop = Graphics.FromImage(cropped);
                gCrop.CompositingQuality = CompositingQuality.HighQuality;
                gCrop.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                gCrop.SmoothingMode      = SmoothingMode.HighQuality;
                gCrop.DrawImage(src,
                    new Rectangle(0, 0, cw, ch),
                    new Rectangle(left, top, cw, ch),
                    GraphicsUnit.Pixel);
                gCrop.Dispose(); gCrop = null;

                resized = new Bitmap(inputSize, inputSize, PixelFormat.Format24bppRgb);
                gResize = Graphics.FromImage(resized);
                gResize.CompositingQuality = CompositingQuality.HighQuality;
                gResize.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                gResize.SmoothingMode      = SmoothingMode.HighQuality;
                gResize.DrawImage(cropped, new Rectangle(0, 0, inputSize, inputSize));
                gResize.Dispose(); gResize = null;

                return BuildTensorFromBitmap(resized, normalize, chanOrder);
            }
            finally
            {
                gCrop?.Dispose();
                gResize?.Dispose();
                resized?.Dispose();
                cropped?.Dispose();
                src?.Dispose();
            }
        }

        private static DenseTensor<float> BuildTensorFromBitmap(
            Bitmap bmp, string normalize, string chanOrder)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            var t = new DenseTensor<float>(new[] { 1, 3, h, w });

            var rect = new Rectangle(0, 0, w, h);
            BitmapData data = null;
            try
            {
                data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int stride = data.Stride;
                var buffer = new byte[stride * h];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                bool wantBgr = (chanOrder ?? "RGB").Trim()
                    .Equals("BGR", StringComparison.OrdinalIgnoreCase);

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 3;
                        // Format24bppRgb is stored as B, G, R in memory.
                        float b = buffer[i];
                        float g = buffer[i + 1];
                        float r = buffer[i + 2];

                        float c0, c1, c2;
                        if (wantBgr) { c0 = b; c1 = g; c2 = r; }
                        else         { c0 = r; c1 = g; c2 = b; }

                        Normalize(ref c0, ref c1, ref c2, normalize);

                        t[0, 0, y, x] = c0;
                        t[0, 1, y, x] = c1;
                        t[0, 2, y, x] = c2;
                    }
                }
            }
            finally
            {
                if (data != null)
                    try { bmp.UnlockBits(data); } catch { /* best effort */ }
            }

            return t;
        }

        // -------------------------------------------------------------------
        // Helpers (unchanged from original)
        // -------------------------------------------------------------------

        private static void Normalize(ref float c0, ref float c1, ref float c2, string mode)
        {
            mode = (mode ?? "0_1").Trim().ToLowerInvariant();

            switch (mode)
            {
                case "none":
                    // Keep 0..255
                    break;

                case "minus1_1":
                    c0 = (c0 / 127.5f) - 1f;
                    c1 = (c1 / 127.5f) - 1f;
                    c2 = (c2 / 127.5f) - 1f;
                    break;

                case "imagenet":
                    c0 = (c0 / 255f - 0.485f) / 0.229f;
                    c1 = (c1 / 255f - 0.456f) / 0.224f;
                    c2 = (c2 / 255f - 0.406f) / 0.225f;
                    break;

                default: // "0_1"
                    c0 /= 255f;
                    c1 /= 255f;
                    c2 /= 255f;
                    break;
            }
        }

        private static List<double> ParseScales(string csv)
        {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(csv)) return list;
            foreach (var p in csv.Split(','))
            {
                if (double.TryParse((p ?? "").Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d) && d > 0.5 && d < 10)
                {
                    list.Add(d);
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
                int streak  = AppSettings.GetInt("Biometrics:Liveness:CircuitFailStreak", 3);
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
