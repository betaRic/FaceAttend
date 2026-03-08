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
    /// ONNX-based liveness detection — sinisigurado kung totoo ang mukha
    /// o isang litrato/video lang (anti-spoofing).
    ///
    /// PHASE 2 FIX (P-02): Tinanggal ang gate lock para sa inference.
    ///
    /// PROBLEMA DATI:
    ///   Ang Monitor.TryEnter(_lock, gateWaitMs) ay nagse-serialize ng LAHAT
    ///   ng liveness inference calls — isa-isa lang. Sa maraming concurrent scans,
    ///   ang mga request ay nagpipila sa lock at naghihintay.
    ///
    /// SOLUSYON:
    ///   Ang Microsoft.ML.OnnxRuntime.InferenceSession.Run() ay THREAD-SAFE
    ///   ayon sa opisyal na dokumentasyon ng ONNX Runtime.
    ///   (https://onnxruntime.ai/docs/api/csharp/)
    ///   Kaya LIGTAS na tawagin ito mula sa maraming threads nang sabay-sabay
    ///   sa IISANG InferenceSession object.
    ///
    ///   Ginagawa natin ngayon:
    ///   - Ang _lock ay GINAGAMIT LANG para sa:
    ///     1. Initialization ng session (_session, _inputName, _outputName)
    ///     2. Circuit breaker state (_failStreak, _circuitUntilUtc, _stuck)
    ///   - Ang inference (session.Run()) ay HINDI na naka-lock —
    ///     maraming threads ang pwedeng mag-run ng inference nang sabay.
    ///
    /// CIRCUIT BREAKER:
    ///   Kung mag-fail ng 3 beses nang magkakasunod (configurable), ang liveness
    ///   check ay dini-disable ng ilang segundo para maiwasan ang cascading failures.
    ///   Pwedeng i-reset ito sa admin dashboard (tingnan ang AdminLivenessController).
    ///
    /// NOTA TUNGKOL SA Task.Run():
    ///   Tinanggal na ang Task.Run() wrapper — hindi na kailangan dahil hindi na
    ///   naka-hold ang lock sa inference. Direkta na ang Run() call sa current thread.
    ///   Mas simple at mas mabilis ito.
    ///
    /// BUG FIX (COMPILE ERROR CS8803 / CS0106 / CS1022):
    ///   Tinanggal ang dalawang dagdag na closing brace `} }` na nasa dulo ng
    ///   ResetCircuit() — ang mga ito ang nagpasara ng class at namespace nang maaga,
    ///   kaya lahat ng method pagkatapos (GetCircuitState, EnsureSession, DisposeSession,
    ///   BuildTensor, Softmax, ParseScales, Fail) ay na-treat bilang top-level statements.
    /// </summary>
    public class OnnxLiveness
    {
        // ─────────────────────────────────────────────────────────────────────
        // Static fields
        // ─────────────────────────────────────────────────────────────────────

        // Ang lock na ito ay para LAMANG sa initialization at circuit breaker state.
        // HINDI na ginagamit para sa inference mismo.
        private static readonly object _lock = new object();

        // Ang ONNX session — initialized minsan at ginagamit ng maraming threads.
        // Volatile para matiyak na nakikita ng lahat ng threads ang bagong value
        // pagkatapos ng initialization.
        private static volatile InferenceSession _session;
        private static string _inputName;
        private static string _outputName;

        // Circuit breaker state — lahat ng ito ay protected ng _lock.
        private static int      _failStreak      = 0;
        private static DateTime _circuitUntilUtc = DateTime.MinValue;
        private static bool     _stuck           = false;

        // ─────────────────────────────────────────────────────────────────────
        // Warm-up
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Nag-pre-load ng ONNX session para ang unang kiosk scan ay hindi mabagal.
        /// Ligtas na tawagin ng maraming beses — ang double-check locking ay
        /// nagtitigarantiya na isa lang ang mag-initialize.
        ///
        /// Tinatawagin ito sa Global.asax Application_Start.
        /// </summary>
        public static void WarmUp()
        {
            lock (_lock)
            {
                try { EnsureSession(); }
                catch (Exception ex)
                {
                    // Log ang error pero huwag mag-crash ang app startup.
                    Trace.TraceError(
                        "[OnnxLiveness.WarmUp] Hindi ma-load ang ONNX model: " + ex.Message);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Main inference entry point
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Siniscore ang liveness ng isang face mula sa isang image file.
        ///
        /// Returns:
        ///   (Ok: true,  Probability: [0..1], Error: null)  — matagumpay
        ///   (Ok: false, Probability: null,   Error: code)  — nabigo
        ///
        /// Error codes:
        ///   CIRCUIT_OPEN    — circuit breaker naka-open (nagre-recover ang system)
        ///   SESSION_STUCK   — nag-timeout ang session dati, kailangan ng restart
        ///   NO_SESSION      — hindi na-initialize ang ONNX model
        ///   PREPROCESS_FAIL — hindi ma-process ang image
        ///   TIMEOUT         — nag-timeout ang inference (malamang nag-hang ang CPU)
        ///   ONNX_ERROR      — general ONNX error
        /// </summary>
        public (bool Ok, float? Probability, string Error) ScoreFromFile(
            string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            var now = DateTime.UtcNow;

            // ── Hakbang 1: Circuit breaker check ─────────────────────────────
            // Ginagawa ito nang mabilis sa labas ng main inference para
            // maiwasang mag-block pa ang mga request kung broken ang circuit.
            lock (_lock)
            {
                if (_stuck)
                    return (false, null, "SESSION_STUCK");
                if (now < _circuitUntilUtc)
                    return (false, null, "CIRCUIT_OPEN");

                // I-initialize ang session kung hindi pa nagagawa.
                try   { EnsureSession(); }
                catch { return Fail("NO_SESSION"); }
            }

            // ── Hakbang 2: Basahin ang configuration ──────────────────────────
            // Binabasa ito sa labas ng lock — ang config values ay effectively
            // read-only pagkatapos ng startup, kaya ligtas ito.
            var inputSize  = AppSettings.GetInt("Biometrics:LivenessInputSize",     128);
            var timeoutMs  = AppSettings.GetInt("Biometrics:Liveness:RunTimeoutMs", 1_500);
            var slowMs     = AppSettings.GetInt("Biometrics:Liveness:SlowMs",       1_200);
            var realIndex  = AppSettings.GetInt("Biometrics:Liveness:RealIndex",    1);

            var cropScale  = AppSettings.GetDouble("Biometrics:Liveness:CropScale",    2.7);
            var normalize  = AppSettings.GetString("Biometrics:Liveness:Normalize",    "0_1");
            var chanOrder  = AppSettings.GetString("Biometrics:Liveness:ChannelOrder", "RGB");
            var outputType = AppSettings.GetString("Biometrics:Liveness:OutputType",   "logits");
            var decision   = AppSettings.GetString("Biometrics:Liveness:Decision",     "max");

            var multiScalesStr = AppSettings.GetString("Biometrics:Liveness:MultiCropScales", "");
            var scales         = ParseScales(multiScalesStr, cropScale);

            // ── Hakbang 3: I-snapshot ang session reference ───────────────────
            // Ginagawa natin ito sa labas ng lock para hindi naka-lock tayo
            // habang nagpo-process.
            //
            // THREAD SAFETY NOTE:
            //   Ang _session ay volatile — guaranteed na ang pinakabagong value
            //   ang nakikita natin dito kahit na walang lock.
            //   Ang InferenceSession.Run() ay thread-safe ayon sa ONNX Runtime docs —
            //   maraming threads ang pwedeng tawagin ito nang sabay sa IISANG session.
            var session    = _session;
            var inputName  = _inputName;
            var outputName = _outputName;

            if (session == null || inputName == null || outputName == null)
                return Fail("NO_SESSION");

            // ── Hakbang 4: Mag-run ng inference ──────────────────────────────
            var sw    = Stopwatch.StartNew();
            var probs = new List<float>(scales.Length);

            try
            {
                foreach (var scale in scales)
                {
                    // Gumawa ng input tensor mula sa image at face box.
                    var tensor = BuildTensor(imagePath, faceBox, inputSize, scale, normalize, chanOrder);
                    if (tensor == null)
                        return Fail("PREPROCESS_FAIL");

                    var inputs = new[]
                    {
                        NamedOnnxValue.CreateFromTensor(inputName, tensor)
                    };

                    // DIREKTANG TAWAG — walang Task.Run, walang gate lock.
                    // Ang Run() ay thread-safe — maraming threads ang pwedeng
                    // tawagin ito nang sabay-sabay sa iisang session.
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

                    // Kinakalkula ang probability ng "REAL" class.
                    float p;
                    if (outputType.Equals("probs", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = Math.Max(0, Math.Min(realIndex, raw.Length - 1));
                        p = raw[idx];
                    }
                    else
                    {
                        // Logits — kailangan ng softmax bago gamitin.
                        p = Softmax(raw)[Math.Max(0, Math.Min(realIndex, raw.Length - 1))];
                    }

                    probs.Add(p);

                    // Timeout check — kung nag-exceed na sa timeoutMs, itigil agad.
                    if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        RecordFailure();
                        return Fail("TIMEOUT");
                    }
                }

                // Pagsamahin ang mga scores mula sa iba't ibang crop scales.
                float finalP = decision.Equals("avg", StringComparison.OrdinalIgnoreCase)
                    ? (probs.Count == 0 ? 0f : probs.Average())
                    : (probs.Count == 0 ? 0f : probs.Max());

                // I-reset ang circuit breaker sa tagumpay.
                lock (_lock) { _failStreak = 0; }

                if (sw.ElapsedMilliseconds >= slowMs)
                {
                    // Mag-log ng mabagal na inference para ma-monitor.
                    Trace.TraceWarning(
                        $"[OnnxLiveness] Mabagal ang inference: {sw.ElapsedMilliseconds}ms " +
                        $"(threshold: {slowMs}ms)");
                }

                return (true, finalP, null);
            }
            catch (Exception ex)
            {
                // I-record ang failure para sa circuit breaker.
                RecordFailure();

                // Log ang error details para sa debugging, pero huwag i-expose
                // sa client response (security hardening).
                Trace.TraceError("[OnnxLiveness.ScoreFromFile] Error: " + ex.Message);

                return Fail("ONNX_ERROR");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Circuit breaker helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Nagtatala ng isang inference failure at nagbubukas ng circuit
        /// kung naabot na ang threshold ng magkakasunod na failures.
        /// </summary>
        private static void RecordFailure()
        {
            lock (_lock)
            {
                var failStreak  = AppSettings.GetInt("Biometrics:Liveness:CircuitFailStreak",      3);
                var disableSecs = AppSettings.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30);

                _failStreak++;
                if (_failStreak >= failStreak)
                {
                    _circuitUntilUtc = DateTime.UtcNow.AddSeconds(disableSecs);
                    Trace.TraceWarning(
                        $"[OnnxLiveness] Circuit breaker naka-open — nag-fail ng {_failStreak}x. " +
                        $"Hindi tatanggapin ang requests hanggang {_circuitUntilUtc:HH:mm:ss} UTC.");
                }
            }
        }

        /// <summary>
        /// Nire-reset ang circuit breaker at stuck flag.
        /// Tinatawagin ito mula sa admin panel (AdminLivenessController.Reset).
        ///
        /// TANDAAN: Huwag dagdagan ng extra closing braces pagkatapos ng method na ito!
        /// Ang nakaraang bug (CS8803) ay dulot ng dalawang extra } } dito na nagpasara
        /// ng class at namespace nang maaga.
        /// </summary>
        public static void ResetCircuit()
        {
            // I-reset ang LAHAT ng 3 circuit breaker fields.
            // Kung isa lang ang nare-reset, mananatiling bukas ang circuit.
            lock (_lock)
            {
                _failStreak      = 0;
                _circuitUntilUtc = DateTime.MinValue;
                _stuck           = false;

                Trace.TraceInformation(
                    "[OnnxLiveness] Circuit breaker manually reset by admin.");
            }
        }   // ← ISANG CLOSING BRACE LANG DITO — huwag dagdag pa!

        /// <summary>
        /// Nagbibigay ng kasalukuyang estado ng circuit breaker.
        /// Ginagamit ng dashboard health check.
        /// </summary>
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

        /// <summary>
        /// Tinitiyak na naka-initialize ang ONNX session.
        /// DAPAT tawagin LAMANG habang hawak ang _lock.
        /// Gumagamit ng fast-path check para maiwasan ang paulit-ulit na initialization.
        /// </summary>
        private static void EnsureSession()
        {
            // Fast path: initialized na — lumabas agad.
            // Ang _session ay volatile kaya safe ang read na ito.
            if (_session != null) return;

            var modelRel  = AppSettings.GetString(
                "Biometrics:LivenessModelPath",
                "~/App_Data/models/liveness/minifasnet.onnx");
            var modelPath = HostingEnvironment.MapPath(modelRel);

            if (string.IsNullOrWhiteSpace(modelPath) || !System.IO.File.Exists(modelPath))
                throw new InvalidOperationException("Hindi mahanap ang liveness model: " + modelRel);

            // Gamitin ang SessionOptions para sa performance.
            var opts = new SessionOptions();
            // Pwedeng dagdagan: opts.IntraOpNumThreads = 2; para limitahan ang CPU usage

            var session    = new InferenceSession(modelPath, opts);
            var inputName  = session.InputMetadata.Keys.First();
            var outputName = session.OutputMetadata.Keys.First();

            // I-assign ang lahat nang atomically para matiyak na consistent
            // ang nakikita ng ibang threads.
            _inputName  = inputName;
            _outputName = outputName;
            _session    = session; // volatile write — visible sa lahat ng threads agad
        }

        /// <summary>
        /// Nililinis ang ONNX InferenceSession at ine-reset ang lahat ng state.
        /// Tinatawagin ito sa Global.asax Application_End.
        /// </summary>
        public static void DisposeSession()
        {
            lock (_lock)
            {
                var s = _session;
                _session    = null; // volatile write — ibang threads ay makikita ito agad
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
        /// Gumagawa ng DenseTensor mula sa isang image file at face box.
        /// Ang tensor ay nasa format na [1, 3, H, W] (batch, channels, height, width).
        ///
        /// Ang faceBox ay pwedeng null — kung wala, gagamitin ang buong larawan.
        ///
        /// Palaging ini-dispose ang mga Bitmap pagkatapos gamitin para maiwasan
        /// ang GDI handle leaks sa maraming concurrent calls.
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
                // Mag-load ng image at mag-crop ng face region.
                full = new Bitmap(imagePath);

                int imgW = full.Width;
                int imgH = full.Height;

                // Kalkulahin ang crop rectangle — mas malawak kaysa sa face box
                // para may context na kasama (mas tumpak ang liveness).
                int cx = (faceBox != null) ? faceBox.Left + faceBox.Width  / 2 : imgW / 2;
                int cy = (faceBox != null) ? faceBox.Top  + faceBox.Height / 2 : imgH / 2;
                int hw = (faceBox != null) ? (int)(faceBox.Width  * cropScale / 2) : imgW / 2;
                int hh = (faceBox != null) ? (int)(faceBox.Height * cropScale / 2) : imgH / 2;

                int x1 = Math.Max(0, cx - hw);
                int y1 = Math.Max(0, cy - hh);
                int x2 = Math.Min(imgW, cx + hw);
                int y2 = Math.Min(imgH, cy + hh);

                if (x2 <= x1 || y2 <= y1)
                    return null; // Invalid na crop rectangle

                var cropRect = new Rectangle(x1, y1, x2 - x1, y2 - y1);
                cropped = full.Clone(cropRect, full.PixelFormat);

                // I-resize papunta sa model input size.
                resized = new Bitmap(inputSize, inputSize);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(cropped, 0, 0, inputSize, inputSize);
                }

                // Gawing tensor: [1, 3, H, W] na float array.
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

                            // GDI+ ay nag-iimbak ng pixels bilang BGR order
                            float b = bytes[i];
                            float g = bytes[i + 1];
                            float r = bytes[i + 2];

                            // Piliin ang channel order batay sa config.
                            float c0 = swapChannels ? b : r;
                            float c1 = g;
                            float c2 = swapChannels ? r : b;

                            // I-normalize ang pixel values batay sa config.
                            if (normalize.Equals("0_1", StringComparison.OrdinalIgnoreCase))
                            {
                                c0 /= 255f; c1 /= 255f; c2 /= 255f;
                            }
                            else if (normalize.Equals("imagenet", StringComparison.OrdinalIgnoreCase))
                            {
                                // ImageNet mean/std normalization
                                c0 = (c0 / 255f - 0.485f) / 0.229f;
                                c1 = (c1 / 255f - 0.456f) / 0.224f;
                                c2 = (c2 / 255f - 0.406f) / 0.225f;
                            }
                            // else: raw (0-255) — hindi na-normalize

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
                // LAGING i-dispose ang mga Bitmap — kahit nag-throw ng exception.
                // Kailangan ito para maiwasan ang GDI handle leaks sa maraming concurrent calls.
                try { resized?.Dispose(); } catch { }
                try { cropped?.Dispose(); } catch { }
                try { full?.Dispose();    } catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Utility helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Nagko-convert ng raw logit scores papunta sa probabilities
        /// gamit ang softmax function.
        /// </summary>
        private static float[] Softmax(float[] logits)
        {
            if (logits == null || logits.Length == 0) return Array.Empty<float>();
            var max  = logits.Max();
            var exps = logits.Select(x => (float)Math.Exp(x - max)).ToArray();
            var sum  = exps.Sum();
            return sum > 0 ? exps.Select(e => e / sum).ToArray() : exps;
        }

        /// <summary>
        /// Nagpa-parse ng comma-separated list ng crop scale values.
        /// Ginagamit para sa multi-scale liveness inference.
        /// Kung walang valid values, ibinabalik ang defaultScale.
        /// </summary>
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

        /// <summary>
        /// Helper para gumawa ng consistent na failed result tuple.
        /// </summary>
        private static (bool Ok, float? Probability, string Error) Fail(string error)
            => (false, null, error);

    }   // ← END OF CLASS OnnxLiveness
}       // ← END OF NAMESPACE FaceAttend.Services.Biometrics
