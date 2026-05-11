using System.IO;
using System.Web.Hosting;

namespace FaceAttend.Services.Helpers
{
    public static class FileSystemHelper
    {
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
    }
}
