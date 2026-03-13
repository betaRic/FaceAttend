using System;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Unified file security service - handles upload security AND validation
    /// MERGED FROM: SecureFileUpload + FileValidationService
    /// </summary>
    public static class FileSecurityService
    {
        // ============================================================================
        // Magic Numbers - SINGLE SOURCE OF TRUTH
        // ============================================================================
        private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF };
        private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] GifMagic87a = { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 };
        private static readonly byte[] GifMagic89a = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        private static readonly byte[] BmpMagic = { 0x42, 0x4D };

        // ============================================================================
        // FILE UPLOAD (from SecureFileUpload)
        // ============================================================================

        /// <summary>
        /// Saves uploaded image securely with validation
        /// </summary>
        public static string SaveTemp(HttpPostedFileBase image, string prefix, int maxBytes)
        {
            ValidateInputs(image, prefix, maxBytes);

            var tempPath = ResolveTempPath();
            var ext = ResolveExtension(image);
            var fileName = GenerateFileName(prefix, ext);
            var fullPath = Path.Combine(tempPath, fileName);

            VerifyPathSafety(fullPath, tempPath);

            image.SaveAs(fullPath);

            // Defense-in-depth: verify the file size on disk
            var actualSize = new FileInfo(fullPath).Length;
            if (actualSize > maxBytes)
            {
                TryDelete(fullPath);
                throw new InvalidOperationException("File too large after save");
            }

            return fullPath;
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private static void ValidateInputs(HttpPostedFileBase image, string prefix, int maxBytes)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 10)
                throw new ArgumentException("prefix is required (max 10 chars)", nameof(prefix));
            if (maxBytes <= 0 || maxBytes > 100 * 1024 * 1024)
                throw new ArgumentException("Invalid maxBytes", nameof(maxBytes));
            if (image.ContentLength <= 0)
                throw new InvalidOperationException("Empty file");
            if (image.ContentLength > maxBytes)
                throw new InvalidOperationException("File too large");
        }

        private static string ResolveTempPath()
        {
            var mapped = HostingEnvironment.MapPath("~/App_Data/tmp");
            if (string.IsNullOrWhiteSpace(mapped))
                throw new InvalidOperationException("TMP_DIR_NOT_FOUND");

            var full = Path.GetFullPath(mapped);
            Directory.CreateDirectory(full);
            return full;
        }

        private static string ResolveExtension(HttpPostedFileBase image)
        {
            var header = ReadHeader(image, 8);

            if (IsJpeg(header)) return ".jpg";
            if (IsPng(header)) return ".png";

            throw new InvalidOperationException("UNSUPPORTED_IMAGE_FORMAT");
        }

        private static string GenerateFileName(string prefix, string extension)
        {
            return prefix + Guid.NewGuid().ToString("N") + extension;
        }

        private static void VerifyPathSafety(string fullPath, string expectedBase)
        {
            var resolvedPath = Path.GetFullPath(fullPath);
            var resolvedBase = Path.GetFullPath(expectedBase);

            var baseWithSep = resolvedBase.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? resolvedBase
                : resolvedBase + Path.DirectorySeparatorChar;

            if (!resolvedPath.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PATH_TRAVERSAL_DETECTED");
        }

        // ============================================================================
        // FILE VALIDATION (from FileValidationService)
        // ============================================================================

        /// <summary>
        /// Validates that the uploaded file is a valid image by checking magic bytes
        /// </summary>
        public static bool IsValidImage(Stream stream, string[] permittedExtensions = null)
        {
            if (stream == null || stream.Length == 0)
                return false;

            try
            {
                byte[] buffer = new byte[8];
                stream.Position = 0;
                int bytesRead = stream.Read(buffer, 0, 8);
                stream.Position = 0;

                if (bytesRead < 3)
                    return false;

                // Check magic bytes
                if (buffer.Take(3).SequenceEqual(JpegMagic))
                    return IsExtensionPermitted(".jpg", ".jpeg", permittedExtensions);

                if (buffer.Take(8).SequenceEqual(PngMagic))
                    return IsExtensionPermitted(".png", permittedExtensions);

                if (buffer.Take(6).SequenceEqual(GifMagic87a) ||
                    buffer.Take(6).SequenceEqual(GifMagic89a))
                    return IsExtensionPermitted(".gif", permittedExtensions);

                if (buffer.Take(2).SequenceEqual(BmpMagic))
                    return IsExtensionPermitted(".bmp", permittedExtensions);

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates image dimensions are within acceptable bounds
        /// </summary>
        public static bool ValidateImageDimensions(Stream stream, int minWidth, int minHeight, int maxWidth, int maxHeight)
        {
            try
            {
                using (var image = System.Drawing.Image.FromStream(stream))
                {
                    return image.Width >= minWidth && image.Width <= maxWidth &&
                           image.Height >= minHeight && image.Height <= maxHeight;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the image format from magic bytes
        /// </summary>
        public static string GetImageFormat(Stream stream)
        {
            if (stream == null || stream.Length == 0)
                return "unknown";

            try
            {
                byte[] buffer = new byte[8];
                stream.Position = 0;
                int bytesRead = stream.Read(buffer, 0, 8);
                stream.Position = 0;

                if (bytesRead < 3)
                    return "unknown";

                if (buffer.Take(3).SequenceEqual(JpegMagic))
                    return "jpeg";
                if (buffer.Take(8).SequenceEqual(PngMagic))
                    return "png";
                if (buffer.Take(6).SequenceEqual(GifMagic87a) ||
                    buffer.Take(6).SequenceEqual(GifMagic89a))
                    return "gif";
                if (buffer.Take(2).SequenceEqual(BmpMagic))
                    return "bmp";

                return "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Validates content type matches actual file content
        /// </summary>
        public static bool IsValidImageFile(HttpPostedFileBase file, string[] permittedExtensions = null)
        {
            if (file == null || file.ContentLength == 0)
                return false;

            // Check by magic bytes
            if (!IsValidImage(file.InputStream, permittedExtensions))
                return false;

            // Additional check: try to load as image
            try
            {
                using (var img = System.Drawing.Image.FromStream(file.InputStream))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // ============================================================================
        // Helper Methods
        // ============================================================================

        private static byte[] ReadHeader(HttpPostedFileBase image, int count)
        {
            var stream = image.InputStream;
            if (stream == null)
                throw new InvalidOperationException("EMPTY_STREAM");

            var buffer = new byte[count];
            long originalPosition = 0;
            bool canSeek = stream.CanSeek;

            if (canSeek)
                originalPosition = stream.Position;

            var read = stream.Read(buffer, 0, buffer.Length);

            if (canSeek)
                stream.Position = originalPosition;

            if (read <= 0)
                throw new InvalidOperationException("EMPTY_STREAM");

            if (read == buffer.Length)
                return buffer;

            var resized = new byte[read];
            Array.Copy(buffer, resized, read);
            return resized;
        }

        private static bool IsJpeg(byte[] header)
        {
            return header != null && header.Length >= 3 &&
                   header[0] == 0xFF &&
                   header[1] == 0xD8 &&
                   header[2] == 0xFF;
        }

        private static bool IsPng(byte[] header)
        {
            return header != null && header.Length >= 8 &&
                   header[0] == 0x89 &&
                   header[1] == 0x50 &&
                   header[2] == 0x4E &&
                   header[3] == 0x47 &&
                   header[4] == 0x0D &&
                   header[5] == 0x0A &&
                   header[6] == 0x1A &&
                   header[7] == 0x0A;
        }

        private static bool IsExtensionPermitted(string extension, string[] permittedExtensions)
        {
            if (permittedExtensions == null || permittedExtensions.Length == 0)
                return true;

            return permittedExtensions.Any(ext =>
                ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsExtensionPermitted(string ext1, string ext2, string[] permittedExtensions)
        {
            if (permittedExtensions == null || permittedExtensions.Length == 0)
                return true;

            return permittedExtensions.Any(ext =>
                ext.Equals(ext1, StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(ext2, StringComparison.OrdinalIgnoreCase));
        }
    }
}
