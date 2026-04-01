using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Hosting;
using FaceAttend.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// ONNX-based liveness detection (MiniAFASNet anti-spoofing).
    /// InferenceSession.Run() is thread-safe per ONNX Runtime docs — multiple concurrent
    /// calls share one session without a lock. _lock is used only for initialization
    /// and circuit breaker state.
    /// Circuit breaker: after N consecutive failures, blocks new requests for M seconds
    /// to prevent cascading failures. Reset via admin dashboard (AdminLivenessController).
    /// </summary>
    public class OnnxLiveness
    {
        // ─────────────────────────────────────────────────────────────────────
        // Static fields
        // ─────────────────────────────────────────────────────────────────────

        // Guards initialization and circuit breaker state only — not inference.
        private static readonly object _lock = new object();

        // volatile: all threads see the latest value after initialization.
        private static volatile InferenceSession _session;
        private static string _inputName;
        private static string _outputName;

        // Circuit breaker state — all protected by _lock.
        private static int      _failStreak      = 0;
        private static DateTime _circuitUntilUtc = DateTime.MinValue;
        private static bool     _stuck           = false;

        // Rate-limited error logging: log first 5, then every 10th (Interlocked, no lock needed).
        private static int _errorLogCounter = 0;
        private const int ErrorLogFirst = 5;
        private const int ErrorLogEvery = 10;

        // ─────────────────────────────────────────────────────────────────────
        // Warm-up
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Pre-loads the ONNX session at Application_Start so the first scan is not slow. Safe to call multiple times.</summary>
        public static void WarmUp()
        {
            lock (_lock)
            {
                try { EnsureSession(); }
                catch (Exception ex)
                {
                    Trace.TraceError(
                        "[OnnxLiveness.WarmUp] Failed to load ONNX model: " + ex.Message);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Main inference entry point
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Scores liveness from an image file.
        /// Returns (Ok:true, Probability:[0..1], Error:null) on success.
        /// Error codes: CIRCUIT_OPEN, SESSION_STUCK, NO_SESSION, PREPROCESS_FAIL, TIMEOUT, ONNX_ERROR.
        /// </summary>
        public (bool Ok, float? Probability, string Error) ScoreFromFile(
            string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            var now = DateTime.UtcNow;

            // Step 1: Circuit breaker check (fast path, before inference).
            lock (_lock)
            {
                if (_stuck)
                    return (false, null, "SESSION_STUCK");
                if (now < _circuitUntilUtc)
                    return (false, null, "CIRCUIT_OPEN");

                try   { EnsureSession(); }
                catch { return Fail("NO_SESSION"); }
            }

            // Step 2: Read config (outside lock — effectively read-only after startup).
            var inputSize  = ConfigurationService.GetInt("Biometrics:LivenessInputSize",     128);
            var timeoutMs  = ConfigurationService.GetInt("Biometrics:Liveness:RunTimeoutMs", 1_500);
            var slowMs     = ConfigurationService.GetInt("Biometrics:Liveness:SlowMs",       1_200);
            var realIndex  = ConfigurationService.GetInt("Biometrics:Liveness:RealIndex",    1);

            var cropScale  = ConfigurationService.GetDouble("Biometrics:Liveness:CropScale",    2.7);
            var normalize  = ConfigurationService.GetString("Biometrics:Liveness:Normalize",    "0_1");
            var chanOrder  = ConfigurationService.GetString("Biometrics:Liveness:ChannelOrder", "RGB");
            normalize = CanonicalNormalize(normalize);
            chanOrder = CanonicalChannelOrder(chanOrder);
            var outputType = ConfigurationService.GetString("Biometrics:Liveness:OutputType",   "logits");
            var decision   = ConfigurationService.GetString("Biometrics:Liveness:Decision",     "max");

            var multiScalesStr = ConfigurationService.GetString("Biometrics:Liveness:MultiCropScales", "");
            var scales         = ParseScales(multiScalesStr, cropScale);

            // Step 3: Snapshot session reference outside lock (_session is volatile; Run() is thread-safe).
            var session    = _session;
            var inputName  = _inputName;
            var outputName = _outputName;

            if (session == null || inputName == null || outputName == null)
                return Fail("NO_SESSION");

            // Step 4: Run inference.
            var sw    = Stopwatch.StartNew();
            var probs = new List<float>(scales.Length);

            try
            {
                foreach (var scale in scales)
                {
                    var tensor = BuildTensor(imagePath, faceBox, inputSize, scale, normalize, chanOrder);
                    if (tensor == null)
                        return Fail("PREPROCESS_FAIL");

                    var inputs = new[]
                    {
                        NamedOnnxValue.CreateFromTensor(inputName, tensor)
                    };

                    float[] raw;
                    using (var results = session.Run(inputs))
                    {
                        var outTensor = results
                            .First(x => x.Name == outputName)
                            .AsTensor<float>();
                        raw = outTensor.ToArray();
                    }

                    if (raw == null || raw.Length < 2)
                        return Fail("BAD_OUTPUT");

                    float p;
                    if (outputType.Equals("probs", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = Math.Max(0, Math.Min(realIndex, raw.Length - 1));
                        p = raw[idx];
                    }
                    else
                    {
                        // Logits — apply softmax first.
                        p = Softmax(raw)[Math.Max(0, Math.Min(realIndex, raw.Length - 1))];
                    }

                    probs.Add(p);

                    if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        RecordFailure();
                        return Fail("TIMEOUT");
                    }
                }

                float finalP = decision.Equals("avg", StringComparison.OrdinalIgnoreCase)
                    ? (probs.Count == 0 ? 0f : probs.Average())
                    : (probs.Count == 0 ? 0f : probs.Max());

                lock (_lock) { _failStreak = 0; }

                if (sw.ElapsedMilliseconds >= slowMs)
                {
                    Trace.TraceWarning(
                        $"[OnnxLiveness] Slow inference: {sw.ElapsedMilliseconds}ms " +
                        $"(threshold: {slowMs}ms)");
                }

                return (true, finalP, null);
            }
            catch (Exception ex)
            {
                RecordFailure();
                var errCount = System.Threading.Interlocked.Increment(ref _errorLogCounter);
                if (errCount <= ErrorLogFirst || errCount % ErrorLogEvery == 0)
                {
                    Trace.TraceError(
                        $"[OnnxLiveness.ScoreFromFile] Error #{errCount}: {ex.ToString()}");
                }

                return Fail("ONNX_ERROR");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // OPTIMIZED: Bitmap-based entry point (single-decode pipeline)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// OPTIMIZED: Run liveness on already-loaded Bitmap — avoids JPEG re-decode.
        /// Use this when you already have the image in memory from detection.
        /// </summary>
        public (bool Ok, float? Probability, string Error) ScoreFromBitmap(
            Bitmap bitmap,
            DlibBiometrics.FaceBox faceBox)
        {
            var now = DateTime.UtcNow;

            // Step 1: Circuit breaker check.
            lock (_lock)
            {
                if (_stuck)
                    return (false, null, "SESSION_STUCK");
                if (now < _circuitUntilUtc)
                    return (false, null, "CIRCUIT_OPEN");

                try   { EnsureSession(); }
                catch { return Fail("NO_SESSION"); }
            }

            // Step 2: Read config.
            var inputSize  = ConfigurationService.GetInt("Biometrics:LivenessInputSize",     128);
            var timeoutMs  = ConfigurationService.GetInt("Biometrics:Liveness:RunTimeoutMs", 1_500);
            var slowMs     = ConfigurationService.GetInt("Biometrics:Liveness:SlowMs",       1_200);
            var realIndex  = ConfigurationService.GetInt("Biometrics:Liveness:RealIndex",    1);

            var cropScale  = ConfigurationService.GetDouble("Biometrics:Liveness:CropScale",    2.7);
            var normalize  = ConfigurationService.GetString("Biometrics:Liveness:Normalize",    "0_1");
            var chanOrder  = ConfigurationService.GetString("Biometrics:Liveness:ChannelOrder", "RGB");
            normalize = CanonicalNormalize(normalize);
            chanOrder = CanonicalChannelOrder(chanOrder);
            var outputType = ConfigurationService.GetString("Biometrics:Liveness:OutputType",   "logits");
            var decision   = ConfigurationService.GetString("Biometrics:Liveness:Decision",     "max");

            var multiScalesStr = ConfigurationService.GetString("Biometrics:Liveness:MultiCropScales", "");
            var scales         = ParseScales(multiScalesStr, cropScale);

            // Step 3: Snapshot session reference.
            var session    = _session;
            var inputName  = _inputName;
            var outputName = _outputName;

            if (session == null || inputName == null || outputName == null)
                return Fail("NO_SESSION");

            // Step 4: Run inference.
            var sw    = Stopwatch.StartNew();
            var probs = new List<float>(scales.Length);

            try
            {
                foreach (var scale in scales)
                {
                    var tensor = BuildTensorFromBitmap(bitmap, faceBox, inputSize, scale, normalize, chanOrder);
                    if (tensor == null)
                        return Fail("PREPROCESS_FAIL");

                    var inputs = new[]
                    {
                        NamedOnnxValue.CreateFromTensor(inputName, tensor)
                    };

                    float[] raw;
                    using (var results = session.Run(inputs))
                    {
                        var outTensor = results
                            .First(x => x.Name == outputName)
                            .AsTensor<float>();
                        raw = outTensor.ToArray();
                    }

                    if (raw == null || raw.Length < 2)
                        return Fail("BAD_OUTPUT");

                    float p;
                    if (outputType.Equals("probs", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = Math.Max(0, Math.Min(realIndex, raw.Length - 1));
                        p = raw[idx];
                    }
                    else
                    {
                        p = Softmax(raw)[Math.Max(0, Math.Min(realIndex, raw.Length - 1))];
                    }

                    probs.Add(p);

                    if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        RecordFailure();
                        return Fail("TIMEOUT");
                    }
                }

                float finalP = decision.Equals("avg", StringComparison.OrdinalIgnoreCase)
                    ? (probs.Count == 0 ? 0f : probs.Average())
                    : (probs.Count == 0 ? 0f : probs.Max());

                lock (_lock) { _failStreak = 0; }

                if (sw.ElapsedMilliseconds >= slowMs)
                {
                    Trace.TraceWarning(
                        $"[OnnxLiveness] Slow inference: {sw.ElapsedMilliseconds}ms " +
                        $"(threshold: {slowMs}ms)");
                }

                return (true, finalP, null);
            }
            catch (Exception ex)
            {
                RecordFailure();

                var errCount = System.Threading.Interlocked.Increment(ref _errorLogCounter);
                if (errCount <= ErrorLogFirst || errCount % ErrorLogEvery == 0)
                {
                    Trace.TraceError(
                        $"[OnnxLiveness.ScoreFromBitmap] Error #{errCount}: {ex.ToString()}");
                }

                return Fail("ONNX_ERROR");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Circuit breaker helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Records an inference failure and opens the circuit breaker if the failure streak threshold is reached.</summary>
        private static void RecordFailure()
        {
            lock (_lock)
            {
                var failStreak  = ConfigurationService.GetInt("Biometrics:Liveness:CircuitFailStreak",      3);
                var disableSecs = ConfigurationService.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30);

                _failStreak++;
                if (_failStreak >= failStreak)
                {
                    _circuitUntilUtc = DateTime.UtcNow.AddSeconds(disableSecs);
                    var msg = $"[OnnxLiveness] Circuit breaker OPEN — {_failStreak} consecutive failures. " +
                              $"Blocking requests until {_circuitUntilUtc:HH:mm:ss} UTC.";

                    Trace.TraceWarning(msg);

                    // Also write to Windows Event Log so sysadmins see it even without the dashboard.
                    try
                    {
                        using (var el = new System.Diagnostics.EventLog("Application"))
                        {
                            el.Source = "FaceAttend";
                            el.WriteEntry(
                                $"Liveness circuit breaker OPENED after {_failStreak} failures. " +
                                $"All scans blocked until {_circuitUntilUtc:HH:mm:ss} UTC. " +
                                "Go to Admin Dashboard to reset manually.",
                                System.Diagnostics.EventLogEntryType.Warning,
                                2001);
                        }
                    }
                    catch { /* Best effort — EventLog source may not be registered yet. */ }
                }
            }
        }

        /// <summary>Resets the circuit breaker. Called from admin panel (AdminLivenessController.Reset).</summary>
        public static void ResetCircuit()
        {
            lock (_lock)
            {
                _failStreak      = 0;
                _circuitUntilUtc = DateTime.MinValue;
                _stuck           = false;

                Trace.TraceInformation(
                    "[OnnxLiveness] Circuit breaker manually reset by admin.");
            }
        }

        /// <summary>Returns the current circuit breaker state for the admin dashboard health check.</summary>
        public static (bool IsOpen, bool IsStuck, DateTime OpenUntilUtc, int FailStreak) GetCircuitState()
        {
            lock (_lock)
            {
                return (
                    IsOpen:       DateTime.UtcNow < _circuitUntilUtc,
                    IsStuck:      _stuck,
                    OpenUntilUtc: _circuitUntilUtc,
                    FailStreak:   _failStreak
                );
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Session lifecycle
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Ensures the ONNX session is initialized. Must be called under _lock.</summary>
        private static void EnsureSession()
        {
            if (_session != null) return; // Fast path — volatile read, safe without lock check.

            var modelRel  = ConfigurationService.GetString(
                "Biometrics:LivenessModelPath",
                "~/App_Data/models/liveness/minifasnet.onnx");
            var modelPath = HostingEnvironment.MapPath(modelRel);

            if (string.IsNullOrWhiteSpace(modelPath) || !System.IO.File.Exists(modelPath))
                throw new InvalidOperationException("Liveness model not found: " + modelRel);

            var opts = new SessionOptions();
            // opts.IntraOpNumThreads = 2; // Uncomment to cap CPU usage per inference.

            var session    = new InferenceSession(modelPath, opts);
            var inputName  = session.InputMetadata.Keys.First();
            var outputName = session.OutputMetadata.Keys.First();

            // Assign all fields before volatile write so other threads see consistent state.
            _inputName  = inputName;
            _outputName = outputName;
            _session    = session; // volatile write
        }

        /// <summary>Disposes the ONNX session and resets all state. Called from Global.asax Application_End.</summary>
        public static void DisposeSession()
        {
            lock (_lock)
            {
                var s = _session;
                _session    = null; // volatile write
                _inputName  = null;
                _outputName = null;
                _stuck      = false;
                _failStreak = 0;
                _circuitUntilUtc = DateTime.MinValue;

                if (s != null)
                {
                    try { s.Dispose(); } catch { /* best effort */ }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Tensor building
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a [1,3,H,W] DenseTensor from an image file and face box.
        /// faceBox may be null — uses the full image in that case.
        /// Always disposes Bitmaps to prevent GDI handle leaks under concurrent load.
        /// </summary>
        private static DenseTensor<float> BuildTensor(
            string imagePath,
            DlibBiometrics.FaceBox faceBox,
            int    inputSize,
            double cropScale,
            string normalize,
            string chanOrder)
        {
            Bitmap full    = null;
            Bitmap cropped = null;
            Bitmap resized = null;

            try
            {
                full = new Bitmap(imagePath);

                int imgW = full.Width;
                int imgH = full.Height;

                // Crop wider than the face box for liveness context.
                int cx = (faceBox != null) ? faceBox.Left + faceBox.Width  / 2 : imgW / 2;
                int cy = (faceBox != null) ? faceBox.Top  + faceBox.Height / 2 : imgH / 2;
                int hw = (faceBox != null) ? (int)(faceBox.Width  * cropScale / 2) : imgW / 2;
                int hh = (faceBox != null) ? (int)(faceBox.Height * cropScale / 2) : imgH / 2;

                int x1 = Math.Max(0, cx - hw);
                int y1 = Math.Max(0, cy - hh);
                int x2 = Math.Min(imgW, cx + hw);
                int y2 = Math.Min(imgH, cy + hh);

                if (x2 <= x1 || y2 <= y1)
                    return null;

                var cropRect = new Rectangle(x1, y1, x2 - x1, y2 - y1);
                cropped = full.Clone(cropRect, full.PixelFormat);

                resized = new Bitmap(inputSize, inputSize);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(cropped, 0, 0, inputSize, inputSize);
                }

                var tensor = new DenseTensor<float>(
                    new[] { 1, 3, inputSize, inputSize });

                bool swapChannels = chanOrder.Equals("BGR", StringComparison.OrdinalIgnoreCase);

                var bmpData = resized.LockBits(
                    new Rectangle(0, 0, inputSize, inputSize),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);

                try
                {
                    int stride     = bmpData.Stride;
                    int bytesTotal = Math.Abs(stride) * inputSize;
                    var bytes      = new byte[bytesTotal];
                    Marshal.Copy(bmpData.Scan0, bytes, 0, bytesTotal);

                    for (int y = 0; y < inputSize; y++)
                    {
                        for (int x = 0; x < inputSize; x++)
                        {
                            int i = y * stride + x * 3;

                            // GDI+ stores pixels as BGR.
                            float b = bytes[i];
                            float g = bytes[i + 1];
                            float r = bytes[i + 2];

                            float c0 = swapChannels ? b : r;
                            float c1 = g;
                            float c2 = swapChannels ? r : b;

                            if (normalize.Equals("0_1", StringComparison.OrdinalIgnoreCase))
                            {
                                c0 /= 255f; c1 /= 255f; c2 /= 255f;
                            }
                            else if (normalize.Equals("minus1_1", StringComparison.OrdinalIgnoreCase))
                            {
                                c0 = c0 / 127.5f - 1f;
                                c1 = c1 / 127.5f - 1f;
                                c2 = c2 / 127.5f - 1f;
                            }
                            else if (normalize.Equals("imagenet", StringComparison.OrdinalIgnoreCase))
                            {
                                // ImageNet mean/std normalization
                                c0 = (c0 / 255f - 0.485f) / 0.229f;
                                c1 = (c1 / 255f - 0.456f) / 0.224f;
                                c2 = (c2 / 255f - 0.406f) / 0.225f;
                            }
                            else if (normalize.Equals("none", StringComparison.OrdinalIgnoreCase))
                            {
                                // Raw 0-255, no-op.
                            }

                            tensor[0, 0, y, x] = c0;
                            tensor[0, 1, y, x] = c1;
                            tensor[0, 2, y, x] = c2;
                        }
                    }
                }
                finally
                {
                    resized.UnlockBits(bmpData);
                }

                return tensor;
            }
            catch (Exception ex)
            {
                Trace.TraceError("[OnnxLiveness.BuildTensor] Error: " + ex.Message);
                return null;
            }
            finally
            {
                try { resized?.Dispose(); } catch { }
                try { cropped?.Dispose(); } catch { }
                try { full?.Dispose();    } catch { }
            }
        }

        /// <summary>
        /// OPTIMIZED: Build tensor directly from Bitmap — no file I/O, no re-decode.
        /// This is the core tensor building logic shared with BuildTensor.
        /// </summary>
        private static DenseTensor<float> BuildTensorFromBitmap(
            Bitmap sourceBitmap,
            DlibBiometrics.FaceBox faceBox,
            int    inputSize,
            double cropScale,
            string normalize,
            string chanOrder)
        {
            Bitmap cropped = null;
            Bitmap resized = null;

            try
            {
                int imgW = sourceBitmap.Width;
                int imgH = sourceBitmap.Height;

                int cx = (faceBox != null) ? faceBox.Left + faceBox.Width  / 2 : imgW / 2;
                int cy = (faceBox != null) ? faceBox.Top  + faceBox.Height / 2 : imgH / 2;
                int hw = (faceBox != null) ? (int)(faceBox.Width  * cropScale / 2) : imgW / 2;
                int hh = (faceBox != null) ? (int)(faceBox.Height * cropScale / 2) : imgH / 2;

                int x1 = Math.Max(0, cx - hw);
                int y1 = Math.Max(0, cy - hh);
                int x2 = Math.Min(imgW, cx + hw);
                int y2 = Math.Min(imgH, cy + hh);

                if (x2 <= x1 || y2 <= y1)
                    return null;

                var cropRect = new Rectangle(x1, y1, x2 - x1, y2 - y1);
                cropped = sourceBitmap.Clone(cropRect, sourceBitmap.PixelFormat);

                resized = new Bitmap(inputSize, inputSize);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(cropped, 0, 0, inputSize, inputSize);
                }

                var tensor = new DenseTensor<float>(
                    new[] { 1, 3, inputSize, inputSize });

                bool swapChannels = chanOrder.Equals("BGR", StringComparison.OrdinalIgnoreCase);

                var bmpData = resized.LockBits(
                    new Rectangle(0, 0, inputSize, inputSize),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);

                try
                {
                    int stride     = bmpData.Stride;
                    int bytesTotal = Math.Abs(stride) * inputSize;
                    var bytes      = new byte[bytesTotal];
                    Marshal.Copy(bmpData.Scan0, bytes, 0, bytesTotal);

                    for (int y = 0; y < inputSize; y++)
                    {
                        for (int x = 0; x < inputSize; x++)
                        {
                            int i = y * stride + x * 3;

                            float b = bytes[i];
                            float g = bytes[i + 1];
                            float r = bytes[i + 2];

                            float c0 = swapChannels ? b : r;
                            float c1 = g;
                            float c2 = swapChannels ? r : b;

                            if (normalize.Equals("0_1", StringComparison.OrdinalIgnoreCase))
                            {
                                c0 /= 255f; c1 /= 255f; c2 /= 255f;
                            }
                            else if (normalize.Equals("minus1_1", StringComparison.OrdinalIgnoreCase))
                            {
                                c0 = c0 / 127.5f - 1f;
                                c1 = c1 / 127.5f - 1f;
                                c2 = c2 / 127.5f - 1f;
                            }
                            else if (normalize.Equals("imagenet", StringComparison.OrdinalIgnoreCase))
                            {
                                c0 = (c0 / 255f - 0.485f) / 0.229f;
                                c1 = (c1 / 255f - 0.456f) / 0.224f;
                                c2 = (c2 / 255f - 0.406f) / 0.225f;
                            }
                            else if (normalize.Equals("none", StringComparison.OrdinalIgnoreCase))
                            {
                                // Raw 0-255, no-op.
                            }

                            tensor[0, 0, y, x] = c0;
                            tensor[0, 1, y, x] = c1;
                            tensor[0, 2, y, x] = c2;
                        }
                    }
                }
                finally
                {
                    resized.UnlockBits(bmpData);
                }

                return tensor;
            }
            catch (Exception ex)
            {
                Trace.TraceError("[OnnxLiveness.BuildTensorFromBitmap] Error: " + ex.Message);
                return null;
            }
            finally
            {
                try { resized?.Dispose(); } catch { }
                try { cropped?.Dispose(); } catch { }
                // sourceBitmap is NOT disposed here — owned by the caller.
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Utility helpers
        // ─────────────────────────────────────────────────────────────────────

        private static string CanonicalNormalize(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            switch (v)
            {
                case "0_1":
                    return "0_1";
                case "minus1_1":
                case "-1_1":
                case "minus1to1":
                case "minus1-to-1":
                    return "minus1_1";
                case "imagenet":
                    return "imagenet";
                case "none":
                case "raw":
                    return "none";
                default:
                    return "0_1";
            }
        }

        private static string CanonicalChannelOrder(string value)
        {
            var v = (value ?? "").Trim().ToUpperInvariant();
            return (v == "BGR" || v == "BRG") ? "BGR" : "RGB";
        }

        /// <summary>Converts raw logit scores to probabilities via softmax.</summary>
        private static float[] Softmax(float[] logits)
        {
            if (logits == null || logits.Length == 0) return Array.Empty<float>();
            var max  = logits.Max();
            var exps = logits.Select(x => (float)Math.Exp(x - max)).ToArray();
            var sum  = exps.Sum();
            return sum > 0 ? exps.Select(e => e / sum).ToArray() : exps;
        }

        /// <summary>Parses a comma-separated list of crop scale values. Returns defaultScale if none are valid.</summary>
        private static double[] ParseScales(string scalesStr, double defaultScale)
        {
            if (string.IsNullOrWhiteSpace(scalesStr))
                return new[] { defaultScale };

            var parts = scalesStr.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Select(s =>
                {
                    double v;
                    return double.TryParse(
                        s,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out v) ? v : defaultScale;
                })
                .Where(v => v > 0)
                .ToArray();

            return parts.Length > 0 ? parts : new[] { defaultScale };
        }

        /// <summary>Returns a consistent failed result tuple.</summary>
        private static (bool Ok, float? Probability, string Error) Fail(string error)
            => (false, null, error);

    }
}
