namespace FaceAttend.Models.ViewModels.Shared
{
    public class CameraViewModel
    {
        public string Id { get; set; }
        public string VideoId { get; set; }
        public bool ShowGuide { get; set; } = true;
        public string GuideText { get; set; } = "";
        public bool Mirror { get; set; } = true;
        public string AspectRatio { get; set; } = "4/3";
        public bool AutoStart { get; set; } = false;
        public string GuideType { get; set; } = "circle"; // circle, oval, rectangle
        public bool IsActive { get; set; } = false;
        public string CssClass { get; set; } = "";
        public bool ShowStatus { get; set; } = true;
        public string InitialStatus { get; set; } = "";
        public int VideoWidth { get; set; } = 0;
        public int VideoHeight { get; set; } = 0;
        public string CameraOptionsJson { get; set; } = "{}";
    }
}
