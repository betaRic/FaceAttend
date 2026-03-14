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

            string tempPath = null;
            string processedPath = null;

            try
            {
                // Save to temp
                tempPath = FileSecurityService.SaveTemp(image, "scan_", maxBytes);

                // Preprocess
                bool isProcessed;
                processedPath = ImagePreprocessor.PreprocessForDetection(tempPath, "scan_", out isProcessed);

                // Initialize services
                var dlib = new DlibBiometrics();
                var liveness = new OnnxLiveness();

                // Build face box from client if provided
                DlibBiometrics.FaceBox faceBox = null;
                if (faceX.HasValue && faceY.HasValue && faceW.HasValue && faceH.HasValue 
                    && faceW.Value > 0 && faceH.Value > 0)
                {
                    faceBox = new DlibBiometrics.FaceBox
                    {
                        Left = faceX.Value,
                        Top = faceY.Value,
                        Width = faceW.Value,
                        Height = faceH.Value
                    };
                }

                // Detect face
                DlibBiometrics.FaceBox detectedBox;
                FaceRecognitionDotNet.Location faceLoc;
                string detectError;

                if (faceBox != null)
                {
                    // Use client-provided box
                    detectedBox = faceBox;
                    faceLoc = new FaceRecognitionDotNet.Location(
                        faceBox.Left, faceBox.Top,
                        faceBox.Left + faceBox.Width,
                        faceBox.Top + faceBox.Height);
                }
                else if (!dlib.TryDetectSingleFaceFromFile(processedPath, out detectedBox, out faceLoc, out detectError))
                {
                    return JsonResponseBuilder.Success(new
                    {
                        ok = false,
                        count = 0,
                        message = "No face detected"
                    });
                }

                // Calculate sharpness
                var sharpness = FaceQualityAnalyzer.CalculateSharpness(processedPath, detectedBox);
                var isMobile = DeviceService.IsMobileDevice(Request);
                var sharpnessThreshold = FaceQualityAnalyzer.GetSharpnessThreshold(isMobile);

                // Liveness check
                var livenessResult = liveness.ScoreFromFile(processedPath, detectedBox);
                var livenessThreshold = (float)ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);

                // Encode face
                double[] encoding;
                string encodeError;
                string base64Encoding = null;

                if (dlib.TryEncodeFromFileWithLocation(processedPath, faceLoc, out encoding, out encodeError) 
                    && encoding != null)
                {
                    base64Encoding = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(encoding));
                }

                // Estimate pose
                var (yaw, pitch) = FaceQualityAnalyzer.EstimatePose(detectedBox, 640, 480);
                var poseBucket = FaceQualityAnalyzer.GetPoseBucket(yaw, pitch);

                // Build response
                return JsonResponseBuilder.Success(new
                {
                    ok = true,
                    count = 1,
                    liveness = livenessResult.Probability ?? 0,
                    livenessOk = livenessResult.Ok && (livenessResult.Probability ?? 0) >= livenessThreshold,
                    livenessThreshold = livenessThreshold,
                    sharpness = sharpness,
                    sharpnessThreshold = sharpnessThreshold,
                    sharpnessOk = sharpness >= sharpnessThreshold,
                    encoding = base64Encoding,
                    poseYaw = yaw,
                    posePitch = pitch,
                    poseBucket = poseBucket,
                    faceBox = new
                    {
                        x = detectedBox.Left,
                        y = detectedBox.Top,
                        w = detectedBox.Width,
                        h = detectedBox.Height
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[ScanController.Frame] Error: {0}", ex);
                return JsonResponseBuilder.Error("SCAN_ERROR", "Face scanning failed");
            }
            finally
            {
                // Cleanup
                ImagePreprocessor.Cleanup(processedPath, tempPath);
                if (tempPath != null)
                {
                    FileSecurityService.TryDelete(tempPath);
                }
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
