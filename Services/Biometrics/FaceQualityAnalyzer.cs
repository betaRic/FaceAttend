using System;
using FaceAttend.Services;

namespace FaceAttend.Services.Biometrics
{
    public static class FaceQualityAnalyzer
    {
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
            OpenVinoBiometrics.FaceBox faceBox, int imageWidth, int imageHeight)
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
            float antiSpoof, float sharpness, int area, float yaw, float pitch)
        {
            var wAntiSpoof  = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:AntiSpoofWeight",  0.40);
            var wSharpness = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:SharpnessWeight", 0.30);
            var wArea      = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:AreaWeight",      0.20);
            var wPose      = (float)ConfigurationService.GetDouble("Biometrics:Enroll:Quality:PoseWeight",      0.10);

            float normSharpness  = Math.Min(sharpness / 300f, 1f);
            float normArea       = Math.Min(area      / 50000f, 1f);
            float poseCentrality = 1f - Math.Min((Math.Abs(yaw) + Math.Abs(pitch)) / 60f, 1f);

            return (antiSpoof       * wAntiSpoof)
                 + (normSharpness  * wSharpness)
                 + (normArea       * wArea)
                 + (poseCentrality * wPose);
        }

        public static float GetSharpnessThreshold(bool isMobile)
        {
            var key = isMobile
                ? "Biometrics:Enroll:SharpnessThreshold:Mobile"
                : "Biometrics:Enroll:SharpnessThreshold";

            return (float)ConfigurationService.GetDouble(key, isMobile ? 28.0 : 35.0);
        }
    }
}
