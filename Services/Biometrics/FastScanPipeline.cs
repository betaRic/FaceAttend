using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Web;
using FaceAttend.Services.Recognition;

namespace FaceAttend.Services.Biometrics
{
    public static class FastScanPipeline
    {
        public class ScanResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public double[] FaceEncoding { get; set; }
            public float AntiSpoofScore { get; set; }
            public bool AntiSpoofOk { get; set; }
            public float[] Landmarks5 { get; set; }
            public string AntiSpoofDecision { get; set; }
            public bool AntiSpoofModelOk { get; set; }
            public OpenVinoBiometrics.FaceBox FaceBox { get; set; }
            public float Sharpness { get; set; }
            public float SharpnessThreshold { get; set; }
            public int ImageWidth { get; set; }
            public int ImageHeight { get; set; }
            public long TimingMs { get; set; }
            public Dictionary<string, long> Timings { get; set; }
        }

        public class EnrollmentScanResult : ScanResult { }

        public static EnrollmentScanResult EnrollmentScanInMemory(
            HttpPostedFileBase image,
            bool isMobile = false)
        {
            var result = Analyze(image, BiometricScanMode.Enrollment, includeTimings: false, isMobile: isMobile);
            return new EnrollmentScanResult
            {
                Ok = result.Ok,
                Error = result.Error,
                FaceEncoding = result.FaceEncoding,
                AntiSpoofScore = result.AntiSpoofScore,
                AntiSpoofOk = result.AntiSpoofOk,
                AntiSpoofDecision = result.AntiSpoofDecision,
                AntiSpoofModelOk = result.AntiSpoofModelOk,
                FaceBox = result.FaceBox,
                Sharpness = result.Sharpness,
                SharpnessThreshold = result.SharpnessThreshold,
                ImageWidth = result.ImageWidth,
                ImageHeight = result.ImageHeight,
                TimingMs = result.TimingMs,
                Timings = result.Timings,
                Landmarks5 = result.Landmarks5
            };
        }

        public static ScanResult ScanInMemory(
            HttpPostedFileBase image,
            bool includeTimings = false,
            bool isMobile = false)
        {
            return Analyze(
                image,
                isMobile ? BiometricScanMode.PublicScan : BiometricScanMode.Kiosk,
                includeTimings,
                isMobile);
        }

        private static ScanResult Analyze(
            HttpPostedFileBase image,
            BiometricScanMode mode,
            bool includeTimings,
            bool isMobile)
        {
            var sw = Stopwatch.StartNew();
            var timings = includeTimings ? new Dictionary<string, long>() : null;

            byte[] imageBytes;
            string loadError;
            if (!ReadImageBytes(image, out imageBytes, out loadError))
                return new ScanResult { Ok = false, Error = loadError, TimingMs = sw.ElapsedMilliseconds, Timings = timings };

            if (timings != null) timings["read_ms"] = sw.ElapsedMilliseconds;

            var biometric = new OpenVinoBiometrics();
            string workerError;
            var response = biometric.AnalyzeBytes(imageBytes, mode, out workerError);
            if (timings != null) timings["openvino_analyze_ms"] = sw.ElapsedMilliseconds;

            if (response == null || !response.Ok)
            {
                return new ScanResult
                {
                    Ok = false,
                    Error = response?.Error ?? workerError ?? "OPENVINO_ANALYZE_FAIL",
                    TimingMs = sw.ElapsedMilliseconds,
                    Timings = timings
                };
            }

            var faceBox = ToFaceBox(response.SelectedFaceBox);
            var antiSpoof = response.AntiSpoof;
            var quality = response.Quality;
            var policy = BiometricPolicy.Current;
            var antiSpoofScore = antiSpoof?.Score ?? 0f;
            var modelOk = antiSpoof == null || antiSpoof.ModelOk;
            var decision = !string.IsNullOrWhiteSpace(antiSpoof?.Decision)
                ? antiSpoof.Decision
                : policy.EvaluateAntiSpoof(modelOk, antiSpoofScore, isMobile).Decision.ToString().ToUpperInvariant();

            int width;
            int height;
            ReadImageDimensions(imageBytes, out width, out height);

            return new ScanResult
            {
                Ok = true,
                FaceEncoding = response.Embedding,
                Landmarks5 = ToLandmarksArray(response),
                AntiSpoofScore = antiSpoofScore,
                AntiSpoofOk = string.Equals(decision, "PASS", StringComparison.OrdinalIgnoreCase),
                AntiSpoofDecision = decision.ToUpperInvariant(),
                AntiSpoofModelOk = modelOk,
                FaceBox = faceBox,
                Sharpness = quality?.Sharpness ?? 0f,
                SharpnessThreshold = quality?.SharpnessThreshold ?? FaceQualityAnalyzer.GetSharpnessThreshold(isMobile),
                ImageWidth = width,
                ImageHeight = height,
                TimingMs = sw.ElapsedMilliseconds,
                Timings = timings
            };
        }

        private static bool ReadImageBytes(HttpPostedFileBase image, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            if (image == null || image.ContentLength <= 0)
            {
                error = "NO_IMAGE";
                return false;
            }

            try
            {
                image.InputStream.Position = 0;
                using (var ms = new MemoryStream())
                {
                    image.InputStream.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                image.InputStream.Position = 0;
                return bytes.Length > 0;
            }
            catch (Exception ex)
            {
                Trace.TraceError("[FastScanPipeline] image read failed: " + ex.Message);
                error = "IMAGE_LOAD_FAIL";
                return false;
            }
        }

        private static void ReadImageDimensions(byte[] bytes, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var image = Image.FromStream(ms))
                {
                    width = image.Width;
                    height = image.Height;
                }
            }
            catch
            {
            }
        }

        private static OpenVinoBiometrics.FaceBox ToFaceBox(WorkerFaceBox faceBox)
        {
            if (faceBox == null)
                return null;

            return new OpenVinoBiometrics.FaceBox
            {
                Left = faceBox.X,
                Top = faceBox.Y,
                Width = faceBox.Width,
                Height = faceBox.Height
            };
        }

        private static float[] ToLandmarksArray(WorkerAnalyzeFaceResponse response)
        {
            if (response?.Landmarks == null)
                return null;

            var items = new List<float>();
            foreach (var point in response.Landmarks)
            {
                items.Add(point.X);
                items.Add(point.Y);
            }

            return items.Count == 0 ? null : items.ToArray();
        }
    }
}
