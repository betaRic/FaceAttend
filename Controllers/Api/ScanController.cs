using System;
using System.IO;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

namespace FaceAttend.Controllers.Api
{
    /// <summary>
    /// Unified face scanning API
    /// Single endpoint for all face detection needs across kiosk, mobile, and admin
    /// </summary>
    [RoutePrefix("api/scan")]
    public class ScanController : Controller
    {
        /// <summary>
        /// Scan a single frame for face detection
        /// Returns liveness score, face encoding, and pose information
        /// </summary>
        [HttpPost]
        [Route("frame")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Frame(HttpPostedFileBase image, 
            int? faceX = null, int? faceY = null, int? faceW = null, int? faceH = null)
        {
            if (image == null || image.ContentLength <= 0)
            {
                return JsonResponseBuilder.Error("NO_IMAGE", "No image provided");
            }

            // Size validation
            var maxBytes = ConfigurationService.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > maxBytes)
            {
                return JsonResponseBuilder.Error("TOO_LARGE", "Image exceeds maximum size");
            }

            // Security: Validate file content
            if (!FileSecurityService.IsValidImage(image.InputStream, new[] { ".jpg", ".jpeg", ".png" }))
            {
                return JsonResponseBuilder.Error("INVALID_FORMAT", "Invalid image format");
            }

            // Build client face box if provided (skips server HOG detection — saves 200-500ms)
            DlibBiometrics.FaceBox clientFaceBox = null;
            if (faceX.HasValue && faceY.HasValue && faceW.HasValue && faceH.HasValue
                && faceW.Value > 0 && faceH.Value > 0)
            {
                clientFaceBox = new DlibBiometrics.FaceBox
                {
                    Left   = faceX.Value,
                    Top    = faceY.Value,
                    Width  = faceW.Value,
                    Height = faceH.Value
                };
            }

            try
            {
                var isMobile = DeviceService.IsMobileDevice(Request);

                // FAST PATH: single-decode, parallel liveness+encode+landmarks
                // Replaces 5 sequential file reads with 1 Bitmap load + parallel ops
                var scan = FastScanPipeline.EnrollmentScanInMemory(image, clientFaceBox, isMobile);

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

                // Pose estimation: landmark-based when available, box-geometry fallback
                float yaw, pitch;
                if (scan.Landmarks5 != null && scan.Landmarks5.Length >= 6)
                    (yaw, pitch) = FaceQualityAnalyzer.EstimatePoseFromLandmarks(scan.Landmarks5);
                else
                    (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(
                        scan.FaceBox, scan.ImageWidth, scan.ImageHeight);
                var poseBucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                // Landmarks response: 3 or 4 points depending on model
                // 3 points: [leftEye, rightEye, noseTip]
                // 4 points: [leftEye, rightEye, noseTip, chin] — when 68-point model succeeded
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
                    liveness         = scan.LivenessScore,
                    livenessOk       = scan.LivenessOk,
                    livenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.65),
                    sharpness        = scan.Sharpness,
                    sharpnessThreshold = scan.SharpnessThreshold,
                    sharpnessOk      = scan.Sharpness >= scan.SharpnessThreshold,
                    encoding         = scan.Base64Encoding,
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

        /// <summary>
        /// Quick validation endpoint - checks if face is valid without full processing
        /// Used for duplicate checking during enrollment
        /// </summary>
        [HttpPost]
        [Route("validate")]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Validate(HttpPostedFileBase image)
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

                var dlib = new DlibBiometrics();
                DlibBiometrics.FaceBox faceBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectError;

                if (!dlib.TryDetectSingleFaceFromFile(tempPath, out faceBox, out faceLoc, out detectError))
                {
                    return JsonResponseBuilder.Success(new
                    {
                        ok = false,
                        isValid = false,
                        message = "No face detected"
                    });
                }

                // Quick encode
                double[] encoding;
                string encodeError;
                if (!dlib.TryEncodeFromFileWithLocation(tempPath, faceLoc, out encoding, out encodeError) 
                    || encoding == null)
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
