using FaceAttend.Services;
using FaceAttend.Services.Security;
using System;
using System.Linq;
using System.Web;

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

                // Read image dimensions so the client can scale the box correctly.
                int imgW = 0, imgH = 0;
                try
                {
                    using (var bmp = new System.Drawing.Bitmap(processedPath))
                    { imgW = bmp.Width; imgH = bmp.Height; }
                }
                catch { }

                var dlib = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(processedPath);
                var count = faces == null ? 0 : faces.Length;

                // Build faceBox from the largest detected face (if any).
                object faceBox = null;
                if (faces != null && faces.Length > 0)
                {
                    var best = faces
                        .OrderByDescending(f => (long)f.Width * f.Height)
                        .First();
                    faceBox = new
                    {
                        x = best.Left,
                        y = best.Top,
                        w = best.Width,
                        h = best.Height,
                        imgW,
                        imgH
                    };
                }

                if (count != 1)
                {
                    return new
                    {
                        ok = true,
                        count,
                        faceBox,
                        liveness = (float?)null,
                        livenessOk = false
                    };
                }

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(processedPath, faces[0]);
                if (!scored.Ok)
                    return new { ok = false, error = scored.Error, count = 1, faceBox };

                var th = (float)SystemConfigService.GetDoubleCached(
                    "Biometrics:LivenessThreshold",
                    AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75));

                var p = scored.Probability ?? 0f;

                return new
                {
                    ok = true,
                    count = 1,
                    faceBox,
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
