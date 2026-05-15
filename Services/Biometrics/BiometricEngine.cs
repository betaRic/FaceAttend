using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Hosting;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FaceAttend.Services.Biometrics
{
    public class BiometricEngine
    {
        public class FaceBox
        {
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Right => Left + Width;
            public int Bottom => Top + Height;
            public int Area => Math.Max(0, Width) * Math.Max(0, Height);
        }

        public static void Initialize()
        {
            var status = OnnxBiometricRuntime.CheckStatus(forceReload: true);
            if (!status.Enabled)
                throw new InvalidOperationException("Biometric engine is disabled. Set Biometrics:Engine:Enabled=true.");
            if (!status.Healthy)
                throw new InvalidOperationException("Biometric engine is not healthy: " + status.Status);
            if (!status.Ready)
                throw new InvalidOperationException("Biometric engine is not scan-ready: " + status.Status);
        }

        public static BiometricEngineStatus GetStatus()
        {
            return OnnxBiometricRuntime.CheckStatus();
        }

        public static BiometricEngineBenchmark Benchmark()
        {
            return OnnxBiometricRuntime.Benchmark();
        }

        public static IEnumerable<string> GetConfiguredModelPaths()
        {
            return OnnxBiometricRuntime.GetConfiguredModelPaths();
        }

        public BiometricAnalysisResponse AnalyzeFile(
            string imagePath,
            BiometricScanMode mode,
            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                error = "BAD_IMAGE_PATH";
                return null;
            }

            try
            {
                return AnalyzeBytes(File.ReadAllBytes(imagePath), mode, out error);
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return null;
            }
        }

        public BiometricAnalysisResponse AnalyzeBytes(
            byte[] imageBytes,
            BiometricScanMode mode,
            out string error)
        {
            error = null;
            if (imageBytes == null || imageBytes.Length == 0)
            {
                error = "NO_IMAGE_BYTES";
                return null;
            }

            try
            {
                return OnnxBiometricRuntime.AnalyzeBytes(imageBytes, mode);
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return null;
            }
        }
    }

    internal static class OnnxBiometricRuntime
    {
        private const string RuntimeName = "ONNX_RUNTIME_CPU";
        private static readonly object StatusSync = new object();
        private static readonly ReaderWriterLockSlim RuntimeLock = new ReaderWriterLockSlim();
        private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(10);

        private static BiometricEngineStatus _cachedStatus;
        private static DateTime _cachedUtc = DateTime.MinValue;
        private static LoadedModelSessions _loaded;

        public static BiometricEngineStatus CheckStatus(bool forceReload = false)
        {
            var now = DateTime.UtcNow;
            if (!forceReload && _cachedStatus != null && (now - _cachedUtc) < StatusCacheTtl)
                return _cachedStatus;

            lock (StatusSync)
            {
                now = DateTime.UtcNow;
                if (!forceReload && _cachedStatus != null && (now - _cachedUtc) < StatusCacheTtl)
                    return _cachedStatus;

                var status = BuildStatus();
                _cachedStatus = status;
                _cachedUtc = now;
                return status;
            }
        }

        public static BiometricAnalysisResponse AnalyzeBytes(byte[] imageBytes, BiometricScanMode mode)
        {
            var status = CheckStatus();
            var policy = BiometricPolicy.Current;
            if (!status.Ready)
                return Fail(status.ErrorCode ?? "BIOMETRIC_ENGINE_NOT_READY", mode, policy);

            RuntimeLock.EnterReadLock();
            try
            {
                if (_loaded == null)
                    return Fail("BIOMETRIC_ENGINE_RUNTIME_NOT_LOADED", mode, policy);

                return _loaded.Analyze(imageBytes, mode, policy);
            }
            catch (Exception ex)
            {
                Trace.TraceError("[BiometricEngine] Analyze failed: " + ex);
                return Fail("BIOMETRIC_ENGINE_ANALYZE_FAIL", mode, policy);
            }
            finally
            {
                RuntimeLock.ExitReadLock();
            }
        }

        public static BiometricEngineBenchmark Benchmark()
        {
            var before = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
            var sw = Stopwatch.StartNew();
            var status = CheckStatus(forceReload: true);
            sw.Stop();
            var after = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);

            return new BiometricEngineBenchmark
            {
                Ok = status.Healthy && status.Ready,
                Runtime = status.Runtime,
                Status = status.Status,
                TotalMs = sw.ElapsedMilliseconds,
                ManagedMemoryBeforeMb = before,
                ManagedMemoryAfterMb = after,
                Engine = status,
                NextRequiredStep = status.Ready
                    ? "Run pilot capture benchmark with real employees, printed-photo spoof, phone-screen spoof, and poor-light captures before fresh enrollment."
                    : "Fix model files/runtime before enrollment."
            };
        }

        public static IEnumerable<string> GetConfiguredModelPaths()
        {
            return BuildSlots().Select(x => x.Path).Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static BiometricEngineStatus BuildStatus()
        {
            var sw = Stopwatch.StartNew();
            var enabled = ConfigurationService.GetBool("Biometrics:Engine:Enabled", true);
            var runtime = ConfigurationService.GetString("Biometrics:Engine:Runtime", RuntimeName);
            var policy = BiometricPolicy.Current;
            var status = new BiometricEngineStatus
            {
                Enabled = enabled,
                Runtime = runtime,
                ModelsDir = ResolveModelsDir(),
                ModelVersion = policy.ModelVersion,
                CheckedUtc = DateTime.UtcNow
            };

            if (!enabled)
            {
                status.Healthy = true;
                status.Ready = false;
                status.ErrorCode = "BIOMETRIC_ENGINE_DISABLED";
                status.Status = "disabled";
                status.DurationMs = sw.ElapsedMilliseconds;
                DisposeLoadedRuntime();
                return status;
            }

            if (!string.Equals(runtime, RuntimeName, StringComparison.OrdinalIgnoreCase))
            {
                status.Healthy = false;
                status.Ready = false;
                status.ErrorCode = "BIOMETRIC_ENGINE_RUNTIME_UNSUPPORTED";
                status.Status = "Unsupported runtime '" + runtime + "'. Use " + RuntimeName + ".";
                status.DurationMs = sw.ElapsedMilliseconds;
                DisposeLoadedRuntime();
                return status;
            }

            var slots = BuildSlots().ToList();
            foreach (var slot in slots)
                status.Models.Add(ProbeModelFile(slot));

            var missing = status.Models.Where(x => !x.Exists).Select(x => x.Slot).ToList();
            var badExtension = status.Models.Where(x => x.Exists && !x.ExtensionOk).Select(x => x.Slot).ToList();

            if (missing.Any())
            {
                status.Healthy = false;
                status.Ready = false;
                status.ErrorCode = "BIOMETRIC_ENGINE_MODELS_MISSING";
                status.Status = "Missing ONNX model files: " + string.Join(", ", missing) + ".";
                status.DurationMs = sw.ElapsedMilliseconds;
                DisposeLoadedRuntime();
                return status;
            }

            if (badExtension.Any())
            {
                status.Healthy = false;
                status.Ready = false;
                status.ErrorCode = "BIOMETRIC_ENGINE_MODEL_FORMAT_UNSUPPORTED";
                status.Status = "Unsupported model format for: " + string.Join(", ", badExtension) + ". Use .onnx files for pure MVC.";
                status.DurationMs = sw.ElapsedMilliseconds;
                DisposeLoadedRuntime();
                return status;
            }

            try
            {
                EnsureLoadedRuntime(slots, status.Models);
            }
            catch (Exception ex)
            {
                status.Healthy = false;
                status.Ready = false;
                status.ErrorCode = "BIOMETRIC_ENGINE_MODEL_LOAD_FAILED";
                status.Status = "ONNX model load failed: " + ex.GetBaseException().Message;
                status.DurationMs = sw.ElapsedMilliseconds;
                return status;
            }

            status.Models.Add(new BiometricModelStatus
            {
                Slot = "landmarks",
                ModelName = ConfigurationService.GetString("Biometrics:LandmarkModel", "yunet-5pt-integrated"),
                Path = "(integrated detector output)",
                Exists = true,
                ExtensionOk = true,
                Loaded = true,
                Inputs = new List<string> { "detector:kps_8/kps_16/kps_32" },
                Outputs = new List<string> { "leftEye,rightEye,nose,leftMouth,rightMouth" }
            });

            status.AnalyzeSupported = true;
            status.Healthy = true;
            status.Ready = true;
            status.Status = "ONNX Runtime biometric engine is ready.";
            status.DurationMs = sw.ElapsedMilliseconds;
            return status;
        }

        private static BiometricModelStatus ProbeModelFile(ModelSlot slot)
        {
            return new BiometricModelStatus
            {
                Slot = slot.Slot,
                ModelName = slot.ModelName,
                Path = slot.Path,
                Exists = File.Exists(slot.Path),
                ExtensionOk = string.Equals(Path.GetExtension(slot.Path), ".onnx", StringComparison.OrdinalIgnoreCase)
            };
        }

        private static void EnsureLoadedRuntime(IList<ModelSlot> slots, IList<BiometricModelStatus> modelStatuses)
        {
            var signature = string.Join("|", slots.Select(x => x.Slot + "=" + x.Path + ":" + File.GetLastWriteTimeUtc(x.Path).Ticks));

            RuntimeLock.EnterUpgradeableReadLock();
            try
            {
                if (_loaded != null && string.Equals(_loaded.Signature, signature, StringComparison.Ordinal))
                {
                    FillModelStatusFromLoaded(modelStatuses, _loaded);
                    return;
                }

                RuntimeLock.EnterWriteLock();
                try
                {
                    if (_loaded != null && string.Equals(_loaded.Signature, signature, StringComparison.Ordinal))
                    {
                        FillModelStatusFromLoaded(modelStatuses, _loaded);
                        return;
                    }

                    var next = LoadedModelSessions.Create(signature, slots);
                    var old = _loaded;
                    _loaded = next;
                    if (old != null) old.Dispose();
                    FillModelStatusFromLoaded(modelStatuses, next);
                }
                finally
                {
                    RuntimeLock.ExitWriteLock();
                }
            }
            finally
            {
                RuntimeLock.ExitUpgradeableReadLock();
            }
        }

        private static void FillModelStatusFromLoaded(IList<BiometricModelStatus> statuses, LoadedModelSessions loaded)
        {
            foreach (var status in statuses)
            {
                BiometricModelStatus source;
                if (!loaded.Metadata.TryGetValue(status.Slot, out source))
                    continue;

                status.Loaded = source.Loaded;
                status.Error = source.Error;
                status.LoadMs = source.LoadMs;
                status.Inputs = source.Inputs;
                status.Outputs = source.Outputs;
            }
        }

        private static SessionOptions CreateSessionOptions()
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };

            var intra = ConfigurationService.GetInt("Biometrics:Engine:IntraOpThreads", Math.Max(1, Environment.ProcessorCount / 2));
            var inter = ConfigurationService.GetInt("Biometrics:Engine:InterOpThreads", 1);
            options.IntraOpNumThreads = Math.Max(1, Math.Min(32, intra));
            options.InterOpNumThreads = Math.Max(1, Math.Min(8, inter));
            return options;
        }

        private static void DisposeLoadedRuntime()
        {
            RuntimeLock.EnterWriteLock();
            try
            {
                if (_loaded != null)
                {
                    _loaded.Dispose();
                    _loaded = null;
                }
            }
            finally
            {
                RuntimeLock.ExitWriteLock();
            }
        }

        private static IEnumerable<ModelSlot> BuildSlots()
        {
            var policy = BiometricPolicy.Current;
            var dir = ResolveModelsDir();
            yield return new ModelSlot("detector", policy.DetectorModel,
                ResolveModelPath(dir, "Biometrics:Engine:DetectorPath", "face-detector.onnx"));
            yield return new ModelSlot("recognizer", policy.RecognizerModel,
                ResolveModelPath(dir, "Biometrics:Engine:RecognizerPath", "face-recognizer.onnx"));
            yield return new ModelSlot("antiSpoof", policy.AntiSpoofModel,
                ResolveModelPath(dir, "Biometrics:Engine:AntiSpoofPath", "anti-spoof.onnx"));
        }

        private static string ResolveModelsDir()
        {
            var configured = ConfigurationService.GetString(
                "Biometrics:ModelDir",
                ConfigurationService.GetString("Biometrics:OnnxModelsDir", "~/App_Data/models/onnx"));

            return MapPath(configured);
        }

        private static string ResolveModelPath(string modelsDir, string key, string fallbackFileName)
        {
            var configured = ConfigurationService.GetString(key, fallbackFileName);
            if (string.IsNullOrWhiteSpace(configured))
                configured = fallbackFileName;

            configured = configured.Trim();
            if (configured.StartsWith("~/", StringComparison.Ordinal))
                return MapPath(configured);
            if (Path.IsPathRooted(configured))
                return configured;

            return Path.Combine(modelsDir ?? "", configured);
        }

        private static string MapPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            if (path.StartsWith("~/", StringComparison.Ordinal))
                return HostingEnvironment.MapPath(path);
            return path;
        }

        private static BiometricAnalysisResponse Fail(string code, BiometricScanMode mode, BiometricPolicy policy)
        {
            return new BiometricAnalysisResponse
            {
                Ok = false,
                Error = code,
                Mode = ModeName(mode),
                ModelVersion = policy.ModelVersion,
                DetectorVersion = policy.DetectorModel,
                RecognizerVersion = policy.RecognizerModel,
                AntiSpoofVersion = policy.AntiSpoofModel
            };
        }

        private static string ModeName(BiometricScanMode mode)
        {
            return mode.ToString().ToUpperInvariant();
        }

        private sealed class ModelSlot
        {
            public ModelSlot(string slot, string modelName, string path)
            {
                Slot = slot;
                ModelName = modelName;
                Path = path;
            }

            public string Slot { get; }
            public string ModelName { get; }
            public string Path { get; }
        }

        private sealed class LoadedModelSessions : IDisposable
        {
            private const int DetectorInputSize = 640;
            private const int FaceInputSize = 112;
            private const int AntiSpoofInputSize = 128;
            private static readonly int[] DetectorStrides = { 8, 16, 32 };

            private static readonly PointF[] FaceAlignmentReference =
            {
                new PointF(38.2946f, 51.6963f),
                new PointF(73.5318f, 51.5014f),
                new PointF(56.0252f, 71.7366f),
                new PointF(41.5493f, 92.3655f),
                new PointF(70.7299f, 92.2041f)
            };

            private readonly InferenceSession _detector;
            private readonly InferenceSession _recognizer;
            private readonly InferenceSession _antiSpoof;
            private readonly string _detectorInputName;
            private readonly string _recognizerInputName;
            private readonly string _recognizerOutputName;
            private readonly string _antiSpoofInputName;
            private readonly string _antiSpoofOutputName;

            private LoadedModelSessions(
                string signature,
                InferenceSession detector,
                InferenceSession recognizer,
                InferenceSession antiSpoof,
                IDictionary<string, BiometricModelStatus> metadata)
            {
                Signature = signature;
                _detector = detector;
                _recognizer = recognizer;
                _antiSpoof = antiSpoof;
                Metadata = metadata;

                _detectorInputName = FirstInput(detector);
                _recognizerInputName = FirstInput(recognizer);
                _recognizerOutputName = FirstOutput(recognizer);
                _antiSpoofInputName = FirstInput(antiSpoof);
                _antiSpoofOutputName = FirstOutput(antiSpoof);
            }

            public string Signature { get; }
            public IDictionary<string, BiometricModelStatus> Metadata { get; }

            public static LoadedModelSessions Create(string signature, IList<ModelSlot> slots)
            {
                InferenceSession detector = null;
                InferenceSession recognizer = null;
                InferenceSession antiSpoof = null;
                var metadata = new Dictionary<string, BiometricModelStatus>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (var slot in slots)
                    {
                        var sw = Stopwatch.StartNew();
                        InferenceSession session;
                        using (var options = CreateSessionOptions())
                            session = new InferenceSession(slot.Path, options);
                        sw.Stop();

                        var modelStatus = BuildLoadedStatus(slot, session, sw.ElapsedMilliseconds);
                        metadata[slot.Slot] = modelStatus;

                        if (string.Equals(slot.Slot, "detector", StringComparison.OrdinalIgnoreCase))
                            detector = session;
                        else if (string.Equals(slot.Slot, "recognizer", StringComparison.OrdinalIgnoreCase))
                            recognizer = session;
                        else if (string.Equals(slot.Slot, "antiSpoof", StringComparison.OrdinalIgnoreCase))
                            antiSpoof = session;
                    }

                    if (detector == null || recognizer == null || antiSpoof == null)
                        throw new InvalidOperationException("Detector, recognizer, and anti-spoof sessions are all required.");

                    ValidateDetector(detector);
                    ValidateRecognizer(recognizer);
                    ValidateAntiSpoof(antiSpoof);

                    return new LoadedModelSessions(signature, detector, recognizer, antiSpoof, metadata);
                }
                catch
                {
                    if (detector != null) detector.Dispose();
                    if (recognizer != null) recognizer.Dispose();
                    if (antiSpoof != null) antiSpoof.Dispose();
                    throw;
                }
            }

            public BiometricAnalysisResponse Analyze(byte[] imageBytes, BiometricScanMode mode, BiometricPolicy policy)
            {
                var sw = Stopwatch.StartNew();
                var timeoutMs = Math.Max(1000, ConfigurationService.GetInt("Biometrics:Engine:AnalyzeTimeoutMs", 5000));

                using (var image = DecodeBitmap(imageBytes))
                {
                    if (image == null || image.Width <= 0 || image.Height <= 0)
                        return Fail("IMAGE_LOAD_FAIL", mode, policy);

                    var detections = DetectFaces(image);
                    if (TimedOut(sw, timeoutMs))
                        return Fail("BIOMETRIC_ENGINE_TIMEOUT", mode, policy);

                    FaceDetection selected;
                    string selectError;
                    if (!TrySelectFace(detections, mode, policy, out selected, out selectError))
                        return Fail(selectError, mode, policy);

                    var aligned = AlignFace(image, selected);
                    if (aligned == null)
                        return Fail("FACE_ALIGN_FAIL", mode, policy);

                    double[] embedding;
                    using (aligned)
                        embedding = Recognize(aligned);

                    if (embedding == null || embedding.Length != policy.EmbeddingDim)
                        return Fail("FACE_RECOGNITION_FAIL", mode, policy);

                    if (TimedOut(sw, timeoutMs))
                        return Fail("BIOMETRIC_ENGINE_TIMEOUT", mode, policy);

                    var antiSpoof = RunAntiSpoofSafely(image, selected, mode, policy);
                    var isMobile = mode == BiometricScanMode.PublicScan || mode == BiometricScanMode.Enrollment;
                    var sharpness = CalculateSharpness(image, selected.Box);
                    var sharpnessThreshold = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);
                    var faceAreaRatio = image.Width <= 0 || image.Height <= 0
                        ? (double?)null
                        : selected.Box.Area / ((double)image.Width * image.Height);

                    var landmarks = selected.ToPolicyLandmarks().ToList();
                    var landmarkArray = ToLandmarkArray(landmarks);
                    var pose = FaceQualityAnalyzer.EstimatePoseFromLandmarks(landmarkArray);
                    var qualityScore = FaceQualityAnalyzer.CalculateQualityScore(
                        antiSpoof.Score,
                        sharpness,
                        selected.Box.Area,
                        pose.yaw,
                        pose.pitch);

                    return new BiometricAnalysisResponse
                    {
                        Ok = true,
                        Mode = ModeName(mode),
                        ModelVersion = policy.ModelVersion,
                        DetectorVersion = policy.DetectorModel,
                        RecognizerVersion = policy.RecognizerModel,
                        AntiSpoofVersion = policy.AntiSpoofModel,
                        FaceCount = detections.Count,
                        SelectedFaceBox = new BiometricFaceBox
                        {
                            X = selected.Box.Left,
                            Y = selected.Box.Top,
                            Width = selected.Box.Width,
                            Height = selected.Box.Height
                        },
                        Landmarks = landmarks.Select(p => new BiometricLandmarkPoint { X = p.X, Y = p.Y }).ToList(),
                        Embedding = embedding,
                        RecognitionCandidates = Enumerable.Empty<BiometricRecognitionCandidate>(),
                        AntiSpoof = antiSpoof,
                        Quality = new BiometricQualityResult
                        {
                            Sharpness = sharpness,
                            SharpnessThreshold = sharpnessThreshold,
                            FaceAreaRatio = faceAreaRatio,
                            Score = qualityScore
                        }
                    };
                }
            }

            public void Dispose()
            {
                _detector.Dispose();
                _recognizer.Dispose();
                _antiSpoof.Dispose();
            }

            private List<FaceDetection> DetectFaces(Bitmap source)
            {
                float scale;
                using (var detectorInput = CreateDetectorImage(source, out scale))
                {
                    var tensor = ToNchwTensor(
                        detectorInput,
                        ChannelOrder.Bgr,
                        null,
                        null);

                    using (var results = Run(_detector, _detectorInputName, tensor))
                    {
                        var outputs = results.ToDictionary(x => x.Name, x => x.AsTensor<float>().ToArray());
                        var detections = DecodeYuNet(outputs, scale, source.Width, source.Height);
                        return NonMaximumSuppression(detections);
                    }
                }
            }

            private double[] Recognize(Bitmap alignedFace)
            {
                var normalize = ConfigurationService.GetBool("Biometrics:Recognizer:Normalize127", false);
                var tensor = ToNchwTensor(
                    alignedFace,
                    ChannelOrder.Rgb,
                    normalize ? new[] { 127.5f, 127.5f, 127.5f } : null,
                    normalize ? new[] { 128f, 128f, 128f } : null);

                using (var results = Run(_recognizer, _recognizerInputName, tensor))
                {
                    var vector = results.First(x => x.Name == _recognizerOutputName)
                        .AsTensor<float>()
                        .ToArray()
                        .Select(x => (double)x)
                        .ToArray();

                    return NormalizeVector(vector);
                }
            }

            private BiometricAntiSpoofResult RunAntiSpoofSafely(
                Bitmap source,
                FaceDetection face,
                BiometricScanMode mode,
                BiometricPolicy policy)
            {
                var isMobile = mode == BiometricScanMode.PublicScan || mode == BiometricScanMode.Enrollment;
                try
                {
                    float score;
                    using (var crop = CreateAntiSpoofCrop(source, face.Box))
                    {
                        var tensor = ToNchwTensor(
                            crop,
                            ChannelOrder.Rgb,
                            new[] { 151.2405f, 119.5950f, 107.8395f },
                            new[] { 63.0105f, 56.4570f, 55.0035f });

                        using (var results = Run(_antiSpoof, _antiSpoofInputName, tensor))
                        {
                            var output = results.First(x => x.Name == _antiSpoofOutputName)
                                .AsTensor<float>()
                                .ToArray();
                            score = ExtractRealFaceScore(output);
                        }
                    }

                    return BuildAntiSpoofResult(policy, score, modelOk: true, isMobile: isMobile);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("[BiometricEngine] Anti-spoof failed: " + ex.GetBaseException().Message);
                    return BuildAntiSpoofResult(policy, 0f, modelOk: false, isMobile: isMobile);
                }
            }

            private static BiometricAntiSpoofResult BuildAntiSpoofResult(
                BiometricPolicy policy,
                float score,
                bool modelOk,
                bool isMobile)
            {
                var decision = policy.EvaluateAntiSpoof(modelOk, score, isMobile);
                return new BiometricAntiSpoofResult
                {
                    Score = score,
                    ClearThreshold = decision.ClearThreshold,
                    ReviewThreshold = decision.ReviewThreshold,
                    BlockThreshold = decision.BlockThreshold,
                    Decision = decision.Decision.ToString().ToUpperInvariant(),
                    Policy = decision.Policy,
                    ModelOk = decision.ModelOk
                };
            }

            private static Bitmap DecodeBitmap(byte[] imageBytes)
            {
                using (var ms = new MemoryStream(imageBytes))
                using (var image = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true))
                {
                    NormalizeExifOrientation(image);

                    var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        SetFastGraphics(g);
                        g.DrawImage(image, 0, 0, image.Width, image.Height);
                    }
                    return bitmap;
                }
            }

            private static void NormalizeExifOrientation(Image image)
            {
                const int orientationId = 0x0112;
                if (image == null || Array.IndexOf(image.PropertyIdList, orientationId) < 0)
                    return;

                try
                {
                    var prop = image.GetPropertyItem(orientationId);
                    if (prop == null || prop.Value == null || prop.Value.Length < 2)
                        return;

                    var orientation = BitConverter.ToUInt16(prop.Value, 0);
                    switch (orientation)
                    {
                        case 2: image.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                        case 3: image.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                        case 4: image.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                        case 5: image.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                        case 6: image.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                        case 7: image.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                        case 8: image.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                    }

                    image.RemovePropertyItem(orientationId);
                }
                catch
                {
                }
            }

            private static Bitmap CreateDetectorImage(Bitmap source, out float scale)
            {
                scale = Math.Min(
                    DetectorInputSize / (float)Math.Max(1, source.Width),
                    DetectorInputSize / (float)Math.Max(1, source.Height));

                var width = Math.Max(1, Math.Min(DetectorInputSize, (int)Math.Round(source.Width * scale)));
                var height = Math.Max(1, Math.Min(DetectorInputSize, (int)Math.Round(source.Height * scale)));
                var canvas = new Bitmap(DetectorInputSize, DetectorInputSize, PixelFormat.Format24bppRgb);

                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Black);
                    SetFastGraphics(g);
                    g.DrawImage(source, 0, 0, width, height);
                }

                return canvas;
            }

            private static Bitmap AlignFace(Bitmap source, FaceDetection face)
            {
                var src = face.ToPolicyLandmarks().ToArray();
                if (src.Length != 5)
                    return null;

                float a;
                float b;
                float tx;
                float ty;
                if (!TryEstimateSimilarity(src, FaceAlignmentReference, out a, out b, out tx, out ty))
                    return null;

                var aligned = new Bitmap(FaceInputSize, FaceInputSize, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(aligned))
                using (var matrix = new Matrix(a, b, -b, a, tx, ty))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighSpeed;
                    g.Transform = matrix;
                    g.DrawImage(source, 0, 0);
                }

                return aligned;
            }

            private static Bitmap CreateAntiSpoofCrop(Bitmap source, BiometricEngine.FaceBox box)
            {
                var scale = (float)ConfigurationService.GetDouble("Biometrics:AntiSpoof:CropScale", 1.5);
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 1f)
                    scale = 1.5f;
                if (scale > 3f)
                    scale = 3f;

                var side = Math.Max(box.Width, box.Height) * scale;
                var centerX = box.Left + box.Width / 2f;
                var centerY = box.Top + box.Height / 2f;
                var crop = RectangleF.FromLTRB(
                    centerX - side / 2f,
                    centerY - side / 2f,
                    centerX + side / 2f,
                    centerY + side / 2f);
                crop = Clamp(crop, source.Width, source.Height);

                var bitmap = new Bitmap(AntiSpoofInputSize, AntiSpoofInputSize, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Black);
                    SetFastGraphics(g);
                    g.DrawImage(
                        source,
                        new Rectangle(0, 0, AntiSpoofInputSize, AntiSpoofInputSize),
                        crop.X,
                        crop.Y,
                        crop.Width,
                        crop.Height,
                        GraphicsUnit.Pixel);
                }
                return bitmap;
            }

            private static DenseTensor<float> ToNchwTensor(
                Bitmap bitmap,
                ChannelOrder order,
                float[] mean,
                float[] scale)
            {
                var tensor = new DenseTensor<float>(new[] { 1, 3, bitmap.Height, bitmap.Width });
                var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                BitmapData data = null;
                try
                {
                    data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    var stride = Math.Abs(data.Stride);
                    var bytes = new byte[stride * bitmap.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

                    for (var y = 0; y < bitmap.Height; y++)
                    {
                        var row = y * stride;
                        for (var x = 0; x < bitmap.Width; x++)
                        {
                            var offset = row + x * 3;
                            var b = bytes[offset];
                            var g = bytes[offset + 1];
                            var r = bytes[offset + 2];

                            if (order == ChannelOrder.Bgr)
                            {
                                tensor[0, 0, y, x] = Normalize(b, 0, mean, scale);
                                tensor[0, 1, y, x] = Normalize(g, 1, mean, scale);
                                tensor[0, 2, y, x] = Normalize(r, 2, mean, scale);
                            }
                            else
                            {
                                tensor[0, 0, y, x] = Normalize(r, 0, mean, scale);
                                tensor[0, 1, y, x] = Normalize(g, 1, mean, scale);
                                tensor[0, 2, y, x] = Normalize(b, 2, mean, scale);
                            }
                        }
                    }
                }
                finally
                {
                    if (data != null)
                        bitmap.UnlockBits(data);
                }

                return tensor;
            }

            private static float Normalize(byte value, int channel, float[] mean, float[] scale)
            {
                var result = (float)value;
                if (mean != null && channel < mean.Length)
                    result -= mean[channel];
                if (scale != null && channel < scale.Length && Math.Abs(scale[channel]) > 0.0001f)
                    result /= scale[channel];
                return result;
            }

            private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(
                InferenceSession session,
                string inputName,
                DenseTensor<float> tensor)
            {
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };
                return session.Run(inputs);
            }

            private static List<FaceDetection> DecodeYuNet(
                IDictionary<string, float[]> outputs,
                float scale,
                int sourceWidth,
                int sourceHeight)
            {
                var scoreThreshold = (float)ConfigurationService.GetDouble("Biometrics:Detect:ScoreThreshold", 0.75);
                var candidates = new List<FaceDetection>();

                foreach (var stride in DetectorStrides)
                {
                    var cls = outputs["cls_" + stride];
                    var obj = outputs["obj_" + stride];
                    var bbox = outputs["bbox_" + stride];
                    var kps = outputs["kps_" + stride];
                    var cols = DetectorInputSize / stride;
                    var rows = DetectorInputSize / stride;

                    for (var r = 0; r < rows; r++)
                    {
                        for (var c = 0; c < cols; c++)
                        {
                            var idx = r * cols + c;
                            var clsScore = Clamp01(cls[idx]);
                            var objScore = Clamp01(obj[idx]);
                            var score = (float)Math.Sqrt(clsScore * objScore);
                            if (score < scoreThreshold)
                                continue;

                            var cx = (c + bbox[idx * 4]) * stride;
                            var cy = (r + bbox[idx * 4 + 1]) * stride;
                            var w = (float)Math.Exp(bbox[idx * 4 + 2]) * stride;
                            var h = (float)Math.Exp(bbox[idx * 4 + 3]) * stride;
                            var left = (cx - w / 2f) / scale;
                            var top = (cy - h / 2f) / scale;
                            var width = w / scale;
                            var height = h / scale;

                            var rect = Clamp(
                                RectangleF.FromLTRB(left, top, left + width, top + height),
                                sourceWidth,
                                sourceHeight);
                            if (rect.Width < 8 || rect.Height < 8)
                                continue;

                            var detection = new FaceDetection
                            {
                                Score = score,
                                Box = new BiometricEngine.FaceBox
                                {
                                    Left = (int)Math.Round(rect.Left),
                                    Top = (int)Math.Round(rect.Top),
                                    Width = (int)Math.Round(rect.Width),
                                    Height = (int)Math.Round(rect.Height)
                                },
                                RightEye = new PointF((kps[idx * 10] + c) * stride / scale, (kps[idx * 10 + 1] + r) * stride / scale),
                                LeftEye = new PointF((kps[idx * 10 + 2] + c) * stride / scale, (kps[idx * 10 + 3] + r) * stride / scale),
                                Nose = new PointF((kps[idx * 10 + 4] + c) * stride / scale, (kps[idx * 10 + 5] + r) * stride / scale),
                                RightMouth = new PointF((kps[idx * 10 + 6] + c) * stride / scale, (kps[idx * 10 + 7] + r) * stride / scale),
                                LeftMouth = new PointF((kps[idx * 10 + 8] + c) * stride / scale, (kps[idx * 10 + 9] + r) * stride / scale)
                            };

                            candidates.Add(detection.ClampLandmarks(sourceWidth, sourceHeight));
                        }
                    }
                }

                return candidates;
            }

            private static List<FaceDetection> NonMaximumSuppression(List<FaceDetection> candidates)
            {
                var nmsThreshold = (float)ConfigurationService.GetDouble("Biometrics:Detect:NmsThreshold", 0.30);
                var topK = Math.Max(1, ConfigurationService.GetInt("Biometrics:Detect:TopK", 50));
                var ordered = candidates
                    .OrderByDescending(x => x.Score)
                    .Take(topK)
                    .ToList();
                var keep = new List<FaceDetection>();

                foreach (var candidate in ordered)
                {
                    var overlaps = keep.Any(existing => IoU(candidate.Box, existing.Box) > nmsThreshold);
                    if (!overlaps)
                        keep.Add(candidate);
                }

                return keep;
            }

            private static bool TrySelectFace(
                List<FaceDetection> detections,
                BiometricScanMode mode,
                BiometricPolicy policy,
                out FaceDetection selected,
                out string error)
            {
                selected = null;
                error = null;

                if (detections == null || detections.Count == 0)
                {
                    error = "NO_FACE";
                    return false;
                }

                if (detections.Count == 1)
                {
                    selected = detections[0];
                    return true;
                }

                if (mode == BiometricScanMode.PublicScan || mode == BiometricScanMode.Enrollment)
                {
                    error = "MULTI_FACE";
                    return false;
                }

                var byArea = detections.OrderByDescending(x => x.Box.Area).ToList();
                var largest = byArea[0];
                var second = byArea[1];
                var ratio = second.Box.Area <= 0 ? double.PositiveInfinity : largest.Box.Area / (double)second.Box.Area;
                if (ratio >= policy.DominantFaceAreaRatio)
                {
                    selected = largest;
                    return true;
                }

                error = "MULTI_FACE_UNCLEAR";
                return false;
            }

            private static bool TryEstimateSimilarity(
                PointF[] src,
                PointF[] dst,
                out float a,
                out float b,
                out float tx,
                out float ty)
            {
                a = b = tx = ty = 0f;
                if (src == null || dst == null || src.Length != dst.Length || src.Length < 2)
                    return false;

                var srcMean = Mean(src);
                var dstMean = Mean(dst);
                double denom = 0;
                double aNum = 0;
                double bNum = 0;

                for (var i = 0; i < src.Length; i++)
                {
                    var sx = src[i].X - srcMean.X;
                    var sy = src[i].Y - srcMean.Y;
                    var dx = dst[i].X - dstMean.X;
                    var dy = dst[i].Y - dstMean.Y;
                    denom += sx * sx + sy * sy;
                    aNum += dx * sx + dy * sy;
                    bNum += dy * sx - dx * sy;
                }

                if (denom < 0.0001)
                    return false;

                a = (float)(aNum / denom);
                b = (float)(bNum / denom);
                tx = dstMean.X - (a * srcMean.X - b * srcMean.Y);
                ty = dstMean.Y - (b * srcMean.X + a * srcMean.Y);
                return true;
            }

            private static PointF Mean(PointF[] points)
            {
                float x = 0;
                float y = 0;
                foreach (var p in points)
                {
                    x += p.X;
                    y += p.Y;
                }

                return new PointF(x / points.Length, y / points.Length);
            }

            private static float ExtractRealFaceScore(float[] output)
            {
                if (output == null || output.Length == 0)
                    return 0f;

                var realIndex = ConfigurationService.GetInt("Biometrics:AntiSpoof:RealClassIndex", 0);
                if (realIndex < 0 || realIndex >= output.Length)
                    realIndex = 0;

                var sum = output.Sum();
                if (sum > 0.98f && sum < 1.02f && output.All(x => x >= 0f && x <= 1f))
                    return Clamp01(output[realIndex]);

                var max = output.Max();
                var exps = output.Select(x => Math.Exp(x - max)).ToArray();
                var denom = exps.Sum();
                return denom <= 0 ? 0f : Clamp01((float)(exps[realIndex] / denom));
            }

            private static float CalculateSharpness(Bitmap source, BiometricEngine.FaceBox box)
            {
                var rect = Clamp(
                    RectangleF.FromLTRB(box.Left, box.Top, box.Right, box.Bottom),
                    source.Width,
                    source.Height);
                if (rect.Width < 8 || rect.Height < 8)
                    return 0f;

                const int size = 160;
                using (var crop = new Bitmap(size, size, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(crop))
                    {
                        SetFastGraphics(g);
                        g.DrawImage(source, new Rectangle(0, 0, size, size), rect.X, rect.Y, rect.Width, rect.Height, GraphicsUnit.Pixel);
                    }

                    return LaplacianVariance(crop);
                }
            }

            private static float LaplacianVariance(Bitmap bitmap)
            {
                var width = bitmap.Width;
                var height = bitmap.Height;
                var gray = new float[width * height];
                var rect = new Rectangle(0, 0, width, height);
                BitmapData data = null;

                try
                {
                    data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    var stride = Math.Abs(data.Stride);
                    var bytes = new byte[stride * height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

                    for (var y = 0; y < height; y++)
                    {
                        var row = y * stride;
                        for (var x = 0; x < width; x++)
                        {
                            var offset = row + x * 3;
                            var b = bytes[offset];
                            var g = bytes[offset + 1];
                            var r = bytes[offset + 2];
                            gray[y * width + x] = 0.114f * b + 0.587f * g + 0.299f * r;
                        }
                    }
                }
                finally
                {
                    if (data != null)
                        bitmap.UnlockBits(data);
                }

                double sum = 0;
                double sumSq = 0;
                var count = 0;
                for (var y = 1; y < height - 1; y++)
                {
                    for (var x = 1; x < width - 1; x++)
                    {
                        var center = gray[y * width + x] * 4f;
                        var lap = center
                                  - gray[y * width + x - 1]
                                  - gray[y * width + x + 1]
                                  - gray[(y - 1) * width + x]
                                  - gray[(y + 1) * width + x];
                        sum += lap;
                        sumSq += lap * lap;
                        count++;
                    }
                }

                if (count == 0)
                    return 0f;

                var mean = sum / count;
                var variance = (sumSq / count) - (mean * mean);
                if (variance < 0) variance = 0;
                return (float)Math.Min(10000, variance);
            }

            private static RectangleF Clamp(RectangleF rect, int width, int height)
            {
                var left = Math.Max(0, Math.Min(width - 1, rect.Left));
                var top = Math.Max(0, Math.Min(height - 1, rect.Top));
                var right = Math.Max(left + 1, Math.Min(width, rect.Right));
                var bottom = Math.Max(top + 1, Math.Min(height, rect.Bottom));
                return RectangleF.FromLTRB(left, top, right, bottom);
            }

            private static float Clamp(float value, float min, float max)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                    return min;
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }

            private static float Clamp01(float value)
            {
                return Clamp(value, 0f, 1f);
            }

            private static float IoU(BiometricEngine.FaceBox a, BiometricEngine.FaceBox b)
            {
                var left = Math.Max(a.Left, b.Left);
                var top = Math.Max(a.Top, b.Top);
                var right = Math.Min(a.Right, b.Right);
                var bottom = Math.Min(a.Bottom, b.Bottom);
                var w = Math.Max(0, right - left);
                var h = Math.Max(0, bottom - top);
                var intersection = w * h;
                var union = a.Area + b.Area - intersection;
                return union <= 0 ? 0f : intersection / (float)union;
            }

            private static double[] NormalizeVector(double[] vector)
            {
                if (vector == null || vector.Length == 0)
                    return vector;

                double sum = 0;
                foreach (var v in vector)
                    sum += v * v;

                var norm = Math.Sqrt(sum);
                if (norm <= 0)
                    return vector;

                for (var i = 0; i < vector.Length; i++)
                    vector[i] /= norm;

                return vector;
            }

            private static float[] ToLandmarkArray(IEnumerable<PointF> points)
            {
                var list = new List<float>();
                foreach (var point in points)
                {
                    list.Add(point.X);
                    list.Add(point.Y);
                }
                return list.ToArray();
            }

            private static bool TimedOut(Stopwatch sw, int timeoutMs)
            {
                return sw != null && sw.ElapsedMilliseconds > timeoutMs;
            }

            private static void SetFastGraphics(Graphics g)
            {
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            }

            private static string FirstInput(InferenceSession session)
            {
                return session.InputMetadata.Keys.First();
            }

            private static string FirstOutput(InferenceSession session)
            {
                return session.OutputMetadata.Keys.First();
            }

            private static BiometricModelStatus BuildLoadedStatus(ModelSlot slot, InferenceSession session, long loadMs)
            {
                return new BiometricModelStatus
                {
                    Slot = slot.Slot,
                    ModelName = slot.ModelName,
                    Path = slot.Path,
                    Exists = true,
                    ExtensionOk = true,
                    Loaded = true,
                    LoadMs = loadMs,
                    Inputs = session.InputMetadata.Keys.ToList(),
                    Outputs = session.OutputMetadata.Keys.ToList()
                };
            }

            private static void ValidateDetector(InferenceSession session)
            {
                var required = new[]
                {
                    "cls_8", "cls_16", "cls_32",
                    "obj_8", "obj_16", "obj_32",
                    "bbox_8", "bbox_16", "bbox_32",
                    "kps_8", "kps_16", "kps_32"
                };

                foreach (var name in required)
                {
                    if (!session.OutputMetadata.ContainsKey(name))
                        throw new InvalidOperationException("Detector output missing: " + name);
                }
            }

            private static void ValidateRecognizer(InferenceSession session)
            {
                if (!session.OutputMetadata.Any())
                    throw new InvalidOperationException("Recognizer has no output.");
            }

            private static void ValidateAntiSpoof(InferenceSession session)
            {
                if (!session.OutputMetadata.Any())
                    throw new InvalidOperationException("Anti-spoof model has no output.");
            }

            private enum ChannelOrder
            {
                Bgr,
                Rgb
            }

            private sealed class FaceDetection
            {
                public BiometricEngine.FaceBox Box { get; set; }
                public float Score { get; set; }
                public PointF RightEye { get; set; }
                public PointF LeftEye { get; set; }
                public PointF Nose { get; set; }
                public PointF RightMouth { get; set; }
                public PointF LeftMouth { get; set; }

                public IEnumerable<PointF> ToPolicyLandmarks()
                {
                    yield return LeftEye;
                    yield return RightEye;
                    yield return Nose;
                    yield return LeftMouth;
                    yield return RightMouth;
                }

                public FaceDetection ClampLandmarks(int width, int height)
                {
                    RightEye = ClampPoint(RightEye, width, height);
                    LeftEye = ClampPoint(LeftEye, width, height);
                    Nose = ClampPoint(Nose, width, height);
                    RightMouth = ClampPoint(RightMouth, width, height);
                    LeftMouth = ClampPoint(LeftMouth, width, height);
                    return this;
                }

                private static PointF ClampPoint(PointF point, int width, int height)
                {
                    return new PointF(
                        Clamp(point.X, 0, Math.Max(0, width - 1)),
                        Clamp(point.Y, 0, Math.Max(0, height - 1)));
                }
            }
        }
    }
}
