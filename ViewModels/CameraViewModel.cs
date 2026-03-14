using System;
using Newtonsoft.Json;

namespace FaceAttend.ViewModels
{
    /// <summary>
    /// View model for the Camera shared component
    /// </summary>
    public class CameraViewModel
    {
        /// <summary>
        /// Unique ID for this camera instance
        /// </summary>
        public string Id { get; set; } = "camera";

        /// <summary>
        /// CSS class to add to container
        /// </summary>
        public string CssClass { get; set; } = "";

        /// <summary>
        /// Whether to show the face guide overlay
        /// </summary>
        public bool ShowGuide { get; set; } = true;

        /// <summary>
        /// Type of guide: "circle", "oval", or "rectangle"
        /// </summary>
        public string GuideType { get; set; } = "circle";

        /// <summary>
        /// Whether to auto-start camera on load
        /// </summary>
        public bool AutoStart { get; set; } = false;

        /// <summary>
        /// Whether camera is currently active
        /// </summary>
        public bool IsActive { get; set; } = false;

        /// <summary>
        /// Whether to show status text
        /// </summary>
        public bool ShowStatus { get; set; } = true;

        /// <summary>
        /// Initial status text
        /// </summary>
        public string InitialStatus { get; set; } = "Camera ready";

        /// <summary>
        /// Video width (0 for auto)
        /// </summary>
        public int VideoWidth { get; set; } = 0;

        /// <summary>
        /// Video height (0 for auto)
        /// </summary>
        public int VideoHeight { get; set; } = 0;

        /// <summary>
        /// Camera options object (facingMode, width, height)
        /// </summary>
        public CameraOptions CameraOptions { get; set; } = new CameraOptions();

        /// <summary>
        /// JSON representation of camera options
        /// </summary>
        public string CameraOptionsJson => 
            JsonConvert.SerializeObject(CameraOptions).Replace("\"", "&quot;");
    }

    /// <summary>
    /// Camera initialization options
    /// </summary>
    public class CameraOptions
    {
        public string FacingMode { get; set; } = "user";
        public VideoConstraint Width { get; set; } = new VideoConstraint { Ideal = 1280 };
        public VideoConstraint Height { get; set; } = new VideoConstraint { Ideal = 720 };
    }

    public class VideoConstraint
    {
        public int? Min { get; set; }
        public int? Ideal { get; set; }
        public int? Max { get; set; }
    }
}
