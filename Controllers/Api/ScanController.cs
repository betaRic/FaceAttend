using System;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers.Api
{
    [RoutePrefix("api/scan")]
    public class ScanController : Controller
    {
        [HttpPost]
        [Route("frame")]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "ApiScanFrame", MaxRequests = 60, WindowSeconds = 60, Burst = 10)]
        public ActionResult Frame(HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
            {
                return JsonResponseBuilder.Error("NO_IMAGE", "No image provided");
            }

            var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > maxBytes)
            {
                return JsonResponseBuilder.Error("TOO_LARGE", "Image exceeds maximum size");
            }

            if (!FileSecurityService.IsValidImage(image.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
            {
                return JsonResponseBuilder.Error("INVALID_FORMAT", "Invalid image format");
            }

            try
            {
                var isMobile = DeviceService.IsMobileDevice(Request);
                var policy = BiometricPolicy.Current;
                var antiSpoofThreshold = policy.AntiSpoofClearThresholdFor(isMobile);
                var scan = FastScanPipeline.EnrollmentScanInMemory(
                    image,
                    isMobile);

                if (!scan.Ok)
                {
                    return JsonResponseBuilder.Success(new
                    {
                        ok    = false,
                        count = 0,
                        error = scan.Error,
                        message = scan.Error == "NO_FACE" ? "No face detected" : scan.Error
                    });
                }

                var antiSpoof = policy.EvaluateAntiSpoof(scan.AntiSpoofModelOk, scan.AntiSpoofScore, isMobile);
                float yaw, pitch;
                if (scan.Landmarks5 != null && scan.Landmarks5.Length >= 6)
                    (yaw, pitch) = FaceQualityAnalyzer.EstimatePoseFromLandmarks(scan.Landmarks5);
                else
                    (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(
                        scan.FaceBox, scan.ImageWidth, scan.ImageHeight);
                var poseBucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                object landmarksResponse = null;
                if (scan.Landmarks5 != null && scan.Landmarks5.Length >= 6)
                {
                    var lm = scan.Landmarks5;
                    bool hasChin = lm.Length >= 8 && lm[7] > 0f;
                    if (hasChin)
                    {
                        landmarksResponse = new object[]
                        {
                            new { x = (int)lm[0], y = (int)lm[1] },  // left eye
                            new { x = (int)lm[2], y = (int)lm[3] },  // right eye
                            new { x = (int)lm[4], y = (int)lm[5] },  // nose tip
                            new { x = (int)lm[6], y = (int)lm[7] }   // chin
                        };
                    }
                    else
                    {
                        landmarksResponse = new object[]
                        {
                            new { x = (int)lm[0], y = (int)lm[1] },
                            new { x = (int)lm[2], y = (int)lm[3] },
                            new { x = (int)lm[4], y = (int)lm[5] }
                        };
                    }
                }

                return JsonResponseBuilder.Success(new
                {
                    ok               = true,
                    count            = 1,
                    antiSpoofScore   = scan.AntiSpoofScore,
                    antiSpoofOk      = antiSpoof.Decision == AntiSpoofDecision.Pass,
                    antiSpoofDecision = antiSpoof.Decision.ToString().ToUpperInvariant(),
                    antiSpoofThreshold = antiSpoofThreshold,
                    sharpness        = scan.Sharpness,
                    sharpnessThreshold = scan.SharpnessThreshold,
                    sharpnessOk      = scan.Sharpness >= scan.SharpnessThreshold,
                    poseYaw          = yaw,
                    posePitch        = pitch,
                    poseBucket       = poseBucket,
                    landmarks        = landmarksResponse,
                    faceBox = scan.FaceBox != null ? (object)new
                    {
                        x = scan.FaceBox.Left,
                        y = scan.FaceBox.Top,
                        w = scan.FaceBox.Width,
                        h = scan.FaceBox.Height
                    } : null,
                    timingMs = scan.TimingMs
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[ScanController.Frame] Error: {0}", ex);
                return JsonResponseBuilder.Error("SCAN_ERROR", "Face scanning failed");
            }
        }

        [HttpPost]
        [Route("validate")]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "ApiScanValidate", MaxRequests = 30, WindowSeconds = 60, Burst = 5)]
        public ActionResult Validate(HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
            {
                return JsonResponseBuilder.Error("NO_IMAGE");
            }

            string tempPath = null;

            try
            {
                var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
                tempPath = FileSecurityService.SaveTemp(image, "val_", maxBytes);

                var biometric = new BiometricEngine();
                string analyzeError;
                var analysis = biometric.AnalyzeFile(tempPath, BiometricScanMode.Enrollment, out analyzeError);

                if (analysis == null || !analysis.Ok || analysis.SelectedFaceBox == null)
                {
                    return JsonResponseBuilder.Success(new
                    {
                        ok = false,
                        isValid = false,
                        message = "No face detected"
                    });
                }

                if (!FaceVectorCodec.IsValidVector(analysis.Embedding))
                {
                    return JsonResponseBuilder.Success(new
                    {
                        ok = false,
                        isValid = false,
                        message = "Could not encode face"
                    });
                }

                return JsonResponseBuilder.Success(new
                {
                    ok = true,
                    isValid = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[ScanController.Validate] Error: {0}", ex);
                return JsonResponseBuilder.Error("VALIDATION_ERROR");
            }
            finally
            {
                if (tempPath != null)
                {
                    FileSecurityService.TryDelete(tempPath);
                }
            }
        }
    }
}
