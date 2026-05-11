using System;
using System.IO;
using FaceAttend.Services.Recognition;

namespace FaceAttend.Services.Biometrics
{
    public class OpenVinoBiometrics
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

        public static void InitializeWorker()
        {
            var health = BiometricWorkerClient.CheckHealth();
            if (!health.Enabled)
                throw new InvalidOperationException("OpenVINO worker is disabled. Set Biometrics:Worker:Enabled=true.");
            if (!health.Healthy)
                throw new InvalidOperationException("OpenVINO worker is not healthy: " + health.Status);
        }

        public static void DisposeWorker()
        {
        }

        public static object GetWorkerStatus()
        {
            var health = BiometricWorkerClient.CheckHealth();
            return new
            {
                enabled = health.Enabled,
                healthy = health.Healthy,
                status = health.Status,
                baseUrl = health.BaseUrl,
                durationMs = health.DurationMs
            };
        }

        public WorkerAnalyzeFaceResponse AnalyzeFile(
            string imagePath,
            BiometricScanMode mode,
            FaceBox faceBoxHint,
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
                return BiometricWorkerClient.AnalyzeFace(File.ReadAllBytes(imagePath), mode, faceBoxHint);
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return null;
            }
        }

        public WorkerAnalyzeFaceResponse AnalyzeBytes(
            byte[] imageBytes,
            BiometricScanMode mode,
            FaceBox faceBoxHint,
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
                return BiometricWorkerClient.AnalyzeFace(imageBytes, mode, faceBoxHint);
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return null;
            }
        }
    }
}
