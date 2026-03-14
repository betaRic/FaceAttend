using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.ViewModels
{
    /// <summary>
    /// View model for the Face Progress shared component
    /// </summary>
    public class FaceProgressViewModel
    {
        /// <summary>
        /// Unique ID for this component
        /// </summary>
        public string Id { get; set; } = "faceProgress";

        /// <summary>
        /// Label text (e.g., "Angles Captured")
        /// </summary>
        public string Label { get; set; } = "Progress";

        /// <summary>
        /// Current number of captured frames
        /// </summary>
        public int Current { get; set; } = 0;

        /// <summary>
        /// Target number of frames
        /// </summary>
        public int Target { get; set; } = 5;

        /// <summary>
        /// Maximum number of dots to show
        /// </summary>
        public int Max { get; set; } = 10;

        /// <summary>
        /// List of captured pose buckets
        /// </summary>
        public List<string> Buckets { get; set; } = new List<string>();

        /// <summary>
        /// Whether to show progress dots
        /// </summary>
        public bool ShowDots { get; set; } = true;

        /// <summary>
        /// Whether to show angle indicators
        /// </summary>
        public bool ShowAngles { get; set; } = true;

        /// <summary>
        /// Whether to show next angle prompt
        /// </summary>
        public bool ShowNextAngle { get; set; } = true;

        /// <summary>
        /// Next angle label text
        /// </summary>
        public string NextAngleLabel { get; set; } = "";

        /// <summary>
        /// Next angle icon class
        /// </summary>
        public string NextAngleIcon { get; set; } = "fa-circle-dot";

        /// <summary>
        /// Calculate completion percentage
        /// </summary>
        public int Percentage => Target > 0 
            ? System.Math.Min(100, (int)((Current / (double)Target) * 100)) 
            : 0;

        /// <summary>
        /// Check if target is reached
        /// </summary>
        public bool IsComplete => Current >= Target;

        /// <summary>
        /// Get missing buckets
        /// </summary>
        public List<string> GetMissingBuckets()
        {
            var required = new[] { "center", "left", "right", "up", "down" };
            return required.Where(r => Buckets == null || !Buckets.Contains(r)).ToList();
        }

        /// <summary>
        /// Get next recommended bucket
        /// </summary>
        public string GetNextBucket()
        {
            return GetMissingBuckets().FirstOrDefault();
        }
    }
}
