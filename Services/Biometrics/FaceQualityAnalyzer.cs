using System;
using System.Drawing;
using FaceAttend.Services;

namespace FaceAttend.Services.Biometrics
{
    public static class FaceQualityAnalyzer
    {
        public static float CalculateSharpnessFromBitmap(Bitmap bitmap, DlibBiometrics.FaceBox faceBox)
        {
            if (bitmap == null || faceBox == null) return 0f;

            try
            {
                int x = Math.Max(0, faceBox.Left);
                int y = Math.Max(0, faceBox.Top);
                int w = Math.Min(faceBox.Width,  bitmap.Width  - x);
                int h = Math.Min(faceBox.Height, bitmap.Height - y);

                if (w <= 0 || h <= 0) return 0f;

                using (var roi   = bitmap.Clone(new Rectangle(x, y, w, h), bitmap.PixelFormat))
                using (var small = new Bitmap(roi, 160, 160))
                {
                    var gray = new float[160 * 160];
                    for (int py = 0; py < 160; py++)
                    for (int px = 0; px < 160; px++)
                    {
                        var c = small.GetPixel(px, py);
                        gray[py * 160 + px] = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                    }

                    float sum = 0f, sumSq = 0f;
                    int count = 0;
                    for (int py = 1; py < 159; py++)
                    for (int px = 1; px < 159; px++)
                    {
                        int i  = py * 160 + px;
                        float lap = -4f * gray[i]
                            + gray[i - 1] + gray[i + 1]
                            + gray[i - 160] + gray[i + 160];
                        sum += lap; sumSq += lap * lap; count++;
                    }
                    if (count == 0) return 0f;
                    float mean = sum / count;
                    return (sumSq / count) - (mean * mean);
                }
            }
            catch { return 0f; }
        }

        public static (float yaw, float pitch) EstimatePoseFromLandmarks(float[] landmarks)
        {
            if (landmarks == null || landmarks.Length < 6)
                return (0f, 0f);

            float leX = landmarks[0], leY = landmarks[1];
            float reX = landmarks[2], reY = landmarks[3];
            float ntX = landmarks[4], ntY = landmarks[5];

            float eyeMidX  = (leX + reX) * 0.5f;
            float eyeMidY  = (leY + reY) * 0.5f;
            float eyeDistX = Math.Abs(reX - leX);
            if (eyeDistX < 1f) eyeDistX = 1f;

            float yaw = ((ntX - eyeMidX) / eyeDistX) * 90f;

            float pitch;
            bool hasChin = landmarks.Length >= 8
                        && landmarks[7] > 0f
                        && (landmarks[7] - eyeMidY) > 10f;

            if (hasChin)
            {
                float chinY      = landmarks[7];
                float faceHeight = chinY - eyeMidY;
                if (faceHeight < 10f) faceHeight = 10f;
                float noseFraction = (ntY - eyeMidY) / faceHeight;
                pitch = -(noseFraction - 0.45f) * 130f;
            }
            else
            {
                float normalizedPitch = (ntY - eyeMidY) / eyeDistX;
                pitch = -(normalizedPitch - 1.05f) * 50f;
            }

            yaw   = Math.Max(-90f, Math.Min(90f, yaw));
            pitch = Math.Max(-90f, Math.Min(90f, pitch));

            return (yaw, pitch);
        }

        public static (float yaw, float pitch) EstimatePose(
            DlibBiometrics.FaceBox faceBox, int imageWidth, int imageHeight)
        {
            if (faceBox == null || imageWidth <= 0 || imageHeight <= 0)
                return (0f, 0f);

            float faceCenterX = (faceBox.Left + faceBox.Width  / 2f) / imageWidth;
            float faceCenterY = (faceBox.Top  + faceBox.Height / 2f) / imageHeight;

            float yaw   = (faceCenterX - 0.5f) * 60f;
            float pitch = (faceCenterY - 0.5f) * 40f;

            return (yaw, pitch);
        }

        public static string GetPoseBucket(float yaw, float pitch)
        {
            yaw = -yaw;

            float absYaw   = Math.Abs(yaw);
            float absPitch = Math.Abs(pitch);

            if (absYaw > 45f || absPitch > 55f) return "other";
            if (absYaw < 18f && absPitch < 28f) return "center";

            if (absYaw >= absPitch)
                return yaw < 0f ? "left" : "right";
            else
                return pitch < 0f ? "up" : "down";
        }

        public static float CalculateQualityScore(
            float liveness, float sharpness, int area, float yaw, float pitch)
        {
            var wLiveness  = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:LivenessWeight",  0.40);
            var wSharpness = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:SharpnessWeight", 0.30);
            var wArea      = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:AreaWeight",      0.20);
            var wPose      = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:PoseWeight",      0.10);

            float normSharpness  = Math.Min(sharpness / 300f, 1f);
            float normArea       = Math.Min(area      / 50000f, 1f);
            float poseCentrality = 1f - Math.Min((Math.Abs(yaw) + Math.Abs(pitch)) / 60f, 1f);

            return (liveness       * wLiveness)
                 + (normSharpness  * wSharpness)
                 + (normArea       * wArea)
                 + (poseCentrality * wPose);
        }

        public static float GetSharpnessThreshold(bool isMobile)
        {
            var key = isMobile
                ? "Biometrics:Enroll:SharpnessThreshold:Mobile"
                : "Biometrics:Enroll:SharpnessThreshold";

            return (float)ConfigurationService.GetDouble(key, isMobile ? 40.0 : 50.0);
        }
    }
}
