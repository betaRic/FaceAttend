using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using FaceAttend.Services;
using FaceAttend.Services.Security;

namespace FaceAttend.Services.Biometrics
{
    /// <summary>
    /// Preprocesses uploaded images before face detection.
    /// Resizes large images early to reduce memory and CPU load.
    /// </summary>
    public static class ImagePreprocessor
    {
        /// <summary>
        /// Returns a path to use for detection. May return the original path.
        /// If it returns a processed temp file, isTemp will be true.
        /// </summary>
        public static string PreprocessForDetection(string sourcePath, string prefix, out bool isTemp)
        {
            isTemp = false;

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return sourcePath;

            var maxDim = AppSettings.GetInt("Biometrics:MaxImageDimension", 1280);
            if (maxDim < 320) maxDim = 320;

            int width, height;
            if (!TryGetDimensionsFast(sourcePath, out width, out height))
                return sourcePath;

            if (width <= maxDim && height <= maxDim)
                return sourcePath;

            Bitmap resized = null;
            try
            {
                resized = ResizeImage(sourcePath, width, height, maxDim);
                if (resized == null)
                    return sourcePath;

                var dir = Path.GetDirectoryName(sourcePath) ?? Path.GetTempPath();
                var tempPath = Path.Combine(dir, string.Format("{0}proc_{1}.jpg", prefix, Guid.NewGuid().ToString("N")));

                SaveJpeg(resized, tempPath, AppSettings.GetInt("Biometrics:PreprocessJpegQuality", 85));

                isTemp = true;
                return tempPath;
            }
            catch
            {
                // Fallback: use original
                return sourcePath;
            }
            finally
            {
                if (resized != null) resized.Dispose();
            }
        }

        public static void Cleanup(string processedPath, string originalPath)
        {
            if (!string.IsNullOrWhiteSpace(processedPath) &&
                !string.Equals(processedPath, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                SecureFileUpload.TryDelete(processedPath);
            }
        }

        private static bool TryGetDimensionsFast(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false))
                {
                    width = img.Width;
                    height = img.Height;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static Bitmap ResizeImage(string path, int origWidth, int origHeight, int maxDim)
        {
            if (origWidth <= 0 || origHeight <= 0)
                return null;

            double ratio = Math.Min((double)maxDim / origWidth, (double)maxDim / origHeight);
            int newWidth = Math.Max(1, (int)(origWidth * ratio));
            int newHeight = Math.Max(1, (int)(origHeight * ratio));

            var resized = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);

            using (var src = new Bitmap(path))
            using (var g = Graphics.FromImage(resized))
            {
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.DrawImage(src, 0, 0, newWidth, newHeight);
            }

            return resized;
        }

        private static void SaveJpeg(Bitmap bmp, string path, int quality)
        {
            if (quality < 40) quality = 40;
            if (quality > 95) quality = 95;

            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

            if (encoder == null)
            {
                bmp.Save(path, ImageFormat.Jpeg);
                return;
            }

            using (var encParams = new EncoderParameters(1))
            {
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                bmp.Save(path, encoder, encParams);
            }
        }
    }
}
