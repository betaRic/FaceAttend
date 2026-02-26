using System;
using System.Web;
using FaceAttend.Services;
using FaceAttend.Services.Security;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Shared ScanFrame pipeline used by admin enrollment and visitor enrollment.
    /// Keeps face detection + liveness behavior consistent to avoid drift.
    /// </summary>
    public static class ScanFramePipeline
    {
        public static object Run(HttpPostedFileBase image, string prefix)
        {
            if (image == null || image.ContentLength <= 0)
                return new { ok = false, error = "NO_IMAGE" };

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return new { ok = false, error = "TOO_LARGE" };

            string path = null;
            string processedPath = null;

            try
            {
                path = SecureFileUpload.SaveTemp(image, prefix, max);

                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(path, prefix, out isProcessed);

                var dlib = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(processedPath);
                var count = faces == null ? 0 : faces.Length;

                if (count != 1)
                {
                    return new
                    {
                        ok = true,
                        count,
                        liveness = (float?)null,
                        livenessOk = false
                    };
                }

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(processedPath, faces[0]);
                if (!scored.Ok)
                    return new { ok = false, error = scored.Error, count = 1 };

                // Use DB-configured threshold when available.
                var th = (float)SystemConfigService.GetDoubleCached(
                    "Biometrics:LivenessThreshold",
                    AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75));

                var p = scored.Probability ?? 0f;

                return new
                {
                    ok = true,
                    count = 1,
                    liveness = p,
                    livenessOk = p >= th
                };
            }
            catch (Exception ex)
            {
                return new { ok = false, error = "SCAN_ERROR", detail = ex.Message };
            }
            finally
            {
                ImagePreprocessor.Cleanup(processedPath, path);
                SecureFileUpload.TryDelete(path);
            }
        }
    }
}
