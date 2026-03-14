using System.Collections.Generic;

namespace FaceAttend.ViewModels
{
    /// <summary>
    /// View model for the FileUploader component
    /// </summary>
    public class FileUploaderViewModel
    {
        /// <summary>
        /// Unique identifier for the uploader instance
        /// </summary>
        public string Id { get; set; } = "uploader";

        /// <summary>
        /// Accepted file types (e.g., "image/*", ".jpg,.png")
        /// </summary>
        public string Accept { get; set; } = "image/*";

        /// <summary>
        /// Maximum number of files allowed
        /// </summary>
        public int MaxFiles { get; set; } = 5;

        /// <summary>
        /// Maximum file size in MB
        /// </summary>
        public int MaxSizeMB { get; set; } = 10;

        /// <summary>
        /// Allowed file extensions for display
        /// </summary>
        public string[] AllowedTypes { get; set; } = new[] { ".jpg", ".jpeg", ".png" };

        /// <summary>
        /// JavaScript callback function name when files are selected
        /// </summary>
        public string OnFilesSelected { get; set; } = "";

        /// <summary>
        /// JavaScript callback function name when an error occurs
        /// </summary>
        public string OnError { get; set; } = "";

        /// <summary>
        /// Dropzone title text
        /// </summary>
        public string Title { get; set; } = "Drop files here or click to browse";

        /// <summary>
        /// Dropzone description text
        /// </summary>
        public string Description { get; set; } = "Select clear, well-lit photos";

        /// <summary>
        /// Visual variant
        /// </summary>
        public UploaderVariant Variant { get; set; } = UploaderVariant.Default;

        /// <summary>
        /// Whether to allow multiple file selection
        /// </summary>
        public bool Multiple => MaxFiles > 1;
    }

    /// <summary>
    /// Visual variants for the uploader component
    /// </summary>
    public enum UploaderVariant
    {
        Default,
        Compact,
        Borderless
    }
}
