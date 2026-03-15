using System.Collections.Generic;

namespace FaceAttend.Models.ViewModels.Shared
{
    public class FaceProgressViewModel
    {
        public string Id { get; set; }
        public string Label { get; set; } = "Angles Captured";
        public int Current { get; set; } = 0;
        public int Target { get; set; } = 5;
        public int Max { get; set; } = 10;
        public int MinFrames { get; set; } = 10;
        public int CurrentFrames { get; set; } = 0;
        public List<string> Buckets { get; set; } = new List<string>();
        public List<string> CapturedBuckets { get; set; } = new List<string>();
        public bool ShowAngles { get; set; } = true;
        public bool ShowDots { get; set; } = true;
        public bool ShowNextAngle { get; set; } = false;
        public string NextAngleLabel { get; set; } = "";
        public string NextAngleIcon { get; set; } = "";
    }
}
