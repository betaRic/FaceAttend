using System;
using System.IO;
using System.Web;
using System.Web.Hosting;

namespace FaceAttend.Services.Security
{
    /// <summary>
    /// Saves uploads into ~/App_Data/tmp using a server-generated name.
    ///
    /// Security goals:
    /// - Never use client filename.
    /// - Force the path to stay inside the temp folder.
    /// - Enforce a size limit.
    /// </summary>
    public static class SecureFileUpload
    {
        public static string SaveTemp(HttpPostedFileBase image, string prefix, int maxBytes)
        {
            ValidateInputs(image, prefix, maxBytes);

            var tempPath = ResolveTempPath();
            var ext = ResolveExtension(image);
            var fileName = GenerateFileName(prefix, ext);
            var fullPath = Path.Combine(tempPath, fileName);

            VerifyPathSafety(fullPath, tempPath);

            image.SaveAs(fullPath);

            // Defense-in-depth: verify the file size on disk.
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
            var ct = (image.ContentType ?? "").Trim().ToLowerInvariant();
            if (ct == "image/png") return ".png";
            if (ct == "image/jpeg" || ct == "image/jpg") return ".jpg";
            return ".jpg";
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
    }
}
