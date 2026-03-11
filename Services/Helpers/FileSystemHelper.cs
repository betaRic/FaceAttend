// =========================================================================
// FILE SYSTEM HELPER - Shared file system utilities
// -------------------------------------------------------------------------
// PANGKARANIWANG PAGGAMIT: File/directory checking na safe
// 
// Ginagamit ng:
//   - DashboardController (model checking)
//   - HealthController (file validation)
// =========================================================================

using System.IO;
using System.Web.Hosting;

namespace FaceAttend.Services.Helpers
{
    public static class FileSystemHelper
    {
        /// <summary>
        /// Checks if a file exists at the given virtual path.
        /// Returns false if path is empty or any error occurs.
        /// </summary>
        public static bool FileExists(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath)) return false;
            try
            {
                var abs = HostingEnvironment.MapPath(virtualPath);
                return !string.IsNullOrEmpty(abs) && File.Exists(abs);
            }
            catch { return false; }
        }

        /// <summary>
        /// Checks if Dlib models are present in the given directory.
        /// Requires at least 2 .dat files.
        /// </summary>
        public static bool DlibModelsPresent(string virtualDir)
        {
            if (string.IsNullOrWhiteSpace(virtualDir)) return false;
            try
            {
                var abs = HostingEnvironment.MapPath(virtualDir);
                if (string.IsNullOrEmpty(abs) || !Directory.Exists(abs)) return false;
                var dat = Directory.GetFiles(abs, "*.dat", SearchOption.TopDirectoryOnly);
                return dat.Length >= 2;
            }
            catch { return false; }
        }

    }
}
