namespace FaceAttend.Models.ViewModels.Shared
{
    public class FileUploaderViewModel
    {
        public string Id { get; set; }
        public string Accept { get; set; } = "image/*";
        public int MaxFiles { get; set; } = 5;
        public int MaxSizeMB { get; set; } = 10;
        public string[] AllowedTypes { get; set; } = new[] { ".jpg", ".jpeg", ".png" };
        public string OnFilesSelected { get; set; } = "";
        public string OnError { get; set; } = "";
        public string Title { get; set; } = "Drop photos here";
        public string Description { get; set; } = "Select clear face photos";
        public bool Multiple { get; set; } = true;
        public UploaderVariant Variant { get; set; } = UploaderVariant.Default;
    }

    public enum UploaderVariant
    {
        Default,
        Compact,
        Borderless
    }
}
