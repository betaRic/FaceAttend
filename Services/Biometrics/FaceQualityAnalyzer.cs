using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using FaceAttend.Services;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Static utilities for computing face image quality signals during enrollment.
    ///
    /// ALL METHODS ARE STATELESS AND THREAD-SAFE.
    ///
    /// Sharpness: Laplacian variance on a 160×160 face ROI crop.
    ///   Computing on full frame is wrong — background texture inflates the score.
    ///   160×160 is ~25x cheaper than 1280×720 with no meaningful accuracy loss.
    ///
    /// Pose: Estimated from FaceBox geometry (eye-region, nose, chin proportions).
    ///   Buckets match JavaScript enrollment-core.js estimatePoseBucket() output.
    ///   IMPORTANT: bucket string literals must match exactly or diversity selection breaks.
    ///
    /// Quality score weights (configurable via Web.config):
    ///   Liveness    0.40  — most important; a fake face is worthless
    ///   Sharpness   0.30  — blurry encoding hurts matching
    ///   Area        0.20  — larger face = more detail for dlib
    ///   Pose        0.10  — frontal is slightly better than extreme angle
    /// </summary>
    public static class FaceQualityAnalyzer
    {
        // ── Sharpness ────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes Laplacian variance on a face region in a Bitmap.
        /// Used by FastScanPipeline for in-memory scanning without file I/O.
        ///
        /// Higher = sharper. Typical good value: 80–300. Below 60 is blurry.
        /// Returns 0 on any error (treat as failed frame).
        /// </summary>
        /// <param name="bitmap">Source bitmap (already loaded in memory).</param>
        /// <param name="faceBox">Bounding box of the detected face.</param>
        public static float CalculateSharpnessFromBitmap(Bitmap bitmap, DlibBiometrics.FaceBox faceBox)
        {
            if (bitmap == null || faceBox == null) return 0f;

            try
            {
                // Clamp ROI to image bounds
                int x = Math.Max(0, faceBox.Left);
                int y = Math.Max(0, faceBox.Top);
                int w = Math.Min(faceBox.Width, bitmap.Width - x);
                int h = Math.Min(faceBox.Height, bitmap.Height - y);

                if (w <= 0 || h <= 0) return 0f;

                // Crop to face ROI and downscale to 160×160 for speed
                using (var roi = bitmap.Clone(new Rectangle(x, y, w, h), bitmap.PixelFormat))
                using (var small = new Bitmap(roi, 160, 160))
                {
                    // Convert to grayscale float array
                    var gray = new float[160 * 160];
                    for (int py = 0; py < 160; py++)
                    for (int px = 0; px < 160; px++)
                    {
                        var c = small.GetPixel(px, py);
                        gray[py * 160 + px] = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                    }

                    // Laplacian variance
                    float sum = 0f, sumSq = 0f;
                    int count = 0;
                    for (int py = 1; py < 159; py++)
                    for (int px = 1; px < 159; px++)
                    {
                        int i = py * 160 + px;
                        float lap = -4f * gray[i]
                            + gray[i - 1] + gray[i + 1]
                            + gray[i - 160] + gray[i + 160];
                        sum += lap;
                        sumSq += lap * lap;
                        count++;
                    }
                    if (count == 0) return 0f;
                    float mean = sum / count;
                    return (sumSq / count) - (mean * mean); // Variance
                }
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Computes Laplacian variance on the face ROI (cropped + downscaled to 160×160).
        /// Higher = sharper. Typical good value: 80–300. Below 60 is blurry.
        /// Returns 0 on any error (treat as failed frame).
        /// </summary>
        /// <param name="imagePath">Full path to temp image file.</param>
        /// <param name="faceBox">Bounding box of the detected face.</param>
        public static float CalculateSharpness(string imagePath, DlibBiometrics.FaceBox faceBox)
        {
            if (imagePath == null || faceBox == null) return 0f;

            try
            {
                using (var full = new Bitmap(imagePath))
                {
                    // Clamp ROI to image bounds
                    int x = Math.Max(0, faceBox.Left);
                    int y = Math.Max(0, faceBox.Top);
                    int w = Math.Min(faceBox.Width, full.Width - x);
                    int h = Math.Min(faceBox.Height, full.Height - y);

                    if (w <= 0 || h <= 0) return 0f;

                    // Crop to face ROI
                    using (var roi = full.Clone(new Rectangle(x, y, w, h), full.PixelFormat))
                    // Downscale to 160×160 for speed (~25x faster than full res)
                    using (var small = new Bitmap(roi, 160, 160))
                    {
                        // Convert to grayscale float array
                        var gray = new float[160 * 160];
                        for (int py = 0; py < 160; py++)
                        for (int px = 0; px < 160; px++)
                        {
                            var c = small.GetPixel(px, py);
                            gray[py * 160 + px] = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                        }

                        // Laplacian variance
                        float sum = 0f, sumSq = 0f;
                        int count = 0;
                        for (int py = 1; py < 159; py++)
                        for (int px = 1; px < 159; px++)
                        {
                            int i = py * 160 + px;
                            float lap = -4f * gray[i]
                                + gray[i - 1] + gray[i + 1]
                                + gray[i - 160] + gray[i + 160];
                            sum += lap;
                            sumSq += lap * lap;
                            count++;
                        }
                        float mean = sum / count;
                        return (sumSq / count) - (mean * mean); // Variance
                    }
                }
            }
            catch
            {
                return 0f;
            }
        }

        // ── Pose ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes yaw and pitch from facial landmarks.
        /// Accepts either 6-float (eyes + nose) or 8-float (eyes + nose + chin) arrays.
        ///
        /// YAW: Nose tip horizontal displacement from eye midpoint, normalized by eye
        ///      distance. Self-scaling — works regardless of face size in frame.
        ///
        /// PITCH (8-float path): Nose fraction of face lower half.
        ///   noseFraction = (noseTip.Y - eyeMid.Y) / (chin.Y - eyeMid.Y)
        ///   Frontal ≈ 0.45. No fixed baseline — fully self-calibrating.
        ///   "Looking up" increases noseFraction; "looking down" decreases it.
        ///
        /// PITCH (6-float fallback): Uses nose-to-eye distance ratio.
        ///   Less reliable — baseline 1.05 was calibrated for average camera distance.
        ///   Use only when chin is not available.
        ///
        /// Bucket thresholds: yaw ±12°, pitch ±12° (relaxed from 10° to reduce noise).
        /// </summary>
        public static (float yaw, float pitch) EstimatePoseFromLandmarks(float[] landmarks)
        {
            if (landmarks == null || landmarks.Length < 6)
                return (0f, 0f);

            float leX = landmarks[0], leY = landmarks[1];
            float reX = landmarks[2], reY = landmarks[3];
            float ntX = landmarks[4], ntY = landmarks[5];

            // Eye midpoint
            float eyeMidX = (leX + reX) * 0.5f;
            float eyeMidY = (leY + reY) * 0.5f;

            // Eye distance (horizontal inter-ocular distance — scale reference)
            float eyeDistX = Math.Abs(reX - leX);
            if (eyeDistX < 1f) eyeDistX = 1f;

            // ── YAW: nose horizontal offset from eye midpoint ──
            // Positive yaw = nose to image right = face turned to person's left
            // In mirrored display: yaw > 0 looks like going right → "right" prompt
            float yaw = ((ntX - eyeMidX) / eyeDistX) * 90f;

            // ── PITCH ──
            float pitch;

            bool hasChin = landmarks.Length >= 8
                        && landmarks[7] > 0f   // chinY > 0 means chin was extracted
                        && landmarks[7] > ntY; // sanity: chin must be below nose in image

            if (hasChin)
            {
                // SELF-CALIBRATING PITCH using chin — no fixed baseline
                // noseFraction = how far nose is between eyes and chin (0=at eyes, 1=at chin)
                float chinY      = landmarks[7];
                float faceHeight = chinY - eyeMidY;
                if (faceHeight < 10f) faceHeight = 10f; // degenerate face box guard

                float noseFraction = (ntY - eyeMidY) / faceHeight;
                // Frontal face → noseFraction ≈ 0.45
                // Looking UP (head tilts back) → noseFraction increases (nose rises relative to chin)
                // Looking DOWN (head tilts forward) → noseFraction decreases
                //
                // pitch sign: negative = looking up, positive = looking down
                // (0.45 - noseFraction): looking up gives positive result, so negate
                pitch = -(noseFraction - 0.45f) * 130f;
            }
            else
            {
                // FALLBACK: nose-to-eye distance ratio (less reliable)
                // The 1.05 baseline was calibrated at ~60cm camera distance.
                // Reduced from 1.2 which was too high for close-up webcam footage.
                float normalizedPitch = (ntY - eyeMidY) / eyeDistX;
                pitch = -(normalizedPitch - 1.05f) * 50f;
            }

            // Clamp to realistic head movement range
            yaw   = Math.Max(-90f, Math.Min(90f, yaw));
            pitch = Math.Max(-90f, Math.Min(90f, pitch));

            return (yaw, pitch);
        }

        /// <summary>
        /// Estimates yaw and pitch from FaceBox geometry.
        /// Uses horizontal asymmetry of the box relative to full image width
        /// as a proxy for head turn. This is a rough estimate — good enough
        /// for bucket classification but not precise degrees.
        ///
        /// Returns (yaw, pitch) in approximate degrees:
        ///   yaw > 0 = face turned right, yaw < 0 = face turned left
        ///   pitch > 0 = face tilted down, pitch < 0 = face tilted up
        /// </summary>
        public static (float yaw, float pitch) EstimatePose(
            DlibBiometrics.FaceBox faceBox, int imageWidth, int imageHeight)
        {
            if (faceBox == null || imageWidth <= 0 || imageHeight <= 0)
                return (0f, 0f);

            // Face center relative to image center, normalized -1..+1
            float faceCenterX = (faceBox.Left + faceBox.Width / 2f) / imageWidth;
            float faceCenterY = (faceBox.Top + faceBox.Height / 2f) / imageHeight;

            // Map center offset to approximate degrees
            float yaw   = (faceCenterX - 0.5f) * 60f;   // ±30° range
            float pitch = (faceCenterY - 0.5f) * 40f;   // ±20° range

            // Aspect ratio hint — REMOVED the -10f bias.
            // The original rule fired on nearly every face (most boxes are >1.4 ratio)
            // and permanently biased pitch to -10, causing "up" to dominate every frame
            // when landmark extraction was unavailable.
            // Box-geometry pitch is inherently unreliable; don't add a constant offset.

            return (yaw, pitch);
        }

        /// <summary>
        /// Classifies (yaw, pitch) into a named pose bucket.
        /// IMPORTANT: Return values must exactly match the JavaScript
        /// estimatePoseBucket() in enrollment-core.js and data-bucket
        /// attributes on diversity dots in _EnrollmentComponent.cshtml.
        /// Valid returns: "center", "left", "right", "up", "down", "other"
        /// "other" means extreme angle — frame should be discarded.
        /// </summary>
        public static string GetPoseBucket(float yaw, float pitch)
        {
            float absYaw   = Math.Abs(yaw);
            float absPitch = Math.Abs(pitch);

            // Extreme angles — discard
            if (absYaw > 30f || absPitch > 25f) return "other";

            // Center zone
            if (absYaw < 10f && absPitch < 10f) return "center";

            // Dominant axis determines bucket
            if (absYaw >= absPitch)
            {
                if (yaw < -10f) return "left";
                if (yaw >  10f) return "right";
            }
            else
            {
                if (pitch < -10f) return "up";
                if (pitch >  10f) return "down";
            }

            return "center"; // Within threshold on both axes
        }

        // ── Quality Score ────────────────────────────────────────────────────────

        /// <summary>
        /// Computes a normalized 0–1 composite quality score.
        /// Weights are read from Web.config at call time (no caching —
        /// they rarely change and this avoids stale config).
        ///
        /// Weights must sum to 1.0. If config is wrong they still work
        /// because each component is individually clamped 0–1.
        /// </summary>
        public static float CalculateQualityScore(
            float liveness, float sharpness, int area, float yaw, float pitch)
        {
            var wLiveness  = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:LivenessWeight",  0.40);
            var wSharpness = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:SharpnessWeight", 0.30);
            var wArea      = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:AreaWeight",      0.20);
            var wPose      = (float)ConfigurationService.GetDouble(
                "Biometrics:Enroll:Quality:PoseWeight",      0.10);

            // Normalize each signal to 0–1
            float normSharpness  = Math.Min(sharpness / 300f, 1f);
            float normArea       = Math.Min(area      / 50000f, 1f);
            float poseCentrality = 1f - Math.Min(
                (Math.Abs(yaw) + Math.Abs(pitch)) / 60f, 1f);

            return (liveness        * wLiveness)
                 + (normSharpness   * wSharpness)
                 + (normArea        * wArea)
                 + (poseCentrality  * wPose);
        }

        // ── Thresholds ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the sharpness threshold appropriate for the context.
        /// Mobile cameras produce lower Laplacian variance than desktop webcams.
        /// </summary>
        public static float GetSharpnessThreshold(bool isMobile)
        {
            var key = isMobile
                ? "Biometrics:Enroll:SharpnessThreshold:Mobile"
                : "Biometrics:Enroll:SharpnessThreshold";

            return (float)ConfigurationService.GetDouble(key, isMobile ? 50.0 : 80.0);
        }
    }
}
