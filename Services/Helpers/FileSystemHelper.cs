using System.IO;
using System.Web.Hosting;

namespace FaceAttend.Services.Helpers
{
    /// <summary>
    /// Centralized file system utility methods.
    /// Consolidates FileExists and directory operations used across the application.
    /// </summary>
    public static class FileSystemHelper
    {
        /// <summary>
        /// Checks if a file exists at the given virtual path.
        /// </summary>
        /// <param name="virtualPath">Virtual path (e.g., ~/Content/file.css)</param>
        /// <returns>True if file exists, false otherwise</returns>
        public static bool FileExists(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath))
                return false;

            try
            {
                var absolutePath = HostingEnvironment.MapPath(virtualPath);
                return !string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if a directory exists at the given virtual path.
        /// </summary>
        /// <param name="virtualPath">Virtual path to directory</param>
        /// <returns>True if directory exists, false otherwise</returns>
        public static bool DirectoryExists(string virtualPath)
        {
            if (string.IsNullOrWhiteSpace(virtualPath))
                return false;

            try
            {
                var absolutePath = HostingEnvironment.MapPath(virtualPath);
                return !string.IsNullOrWhiteSpace(absolutePath) && Directory.Exists(absolutePath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Safely deletes a file if it exists.
        /// </summary>
        /// <param name="filePath">Absolute file path</param>
        /// <returns>True if deleted or didn't exist, false if error occurred</returns>
        public static bool SafeDelete(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return true;

            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
