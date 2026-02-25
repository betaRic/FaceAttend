using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Web.Mvc;

namespace FaceAttend.Areas.Admin.Models
{
    public class SettingsVm
    {
        // ─── Biometrics ────────────────────────────────────────────────────────────

        [Range(0.20, 1.00)]
        [Display(Name = "Face match tolerance")]
        public double DlibTolerance { get; set; }

        [Range(0.00, 1.00)]
        [Display(Name = "Liveness threshold")]
        public double LivenessThreshold { get; set; }

        // ─── Advanced liveness ─────────────────────────────────────────────────────

        [Display(Name = "Liveness decision")]
        public string LivenessDecision { get; set; }

        [Display(Name = "Multi-crop scales")]
        public string LivenessMultiCropScales { get; set; }

        [Range(64, 512)]
        [Display(Name = "Liveness input size")]
        public int LivenessInputSize { get; set; }

        [Range(1.0, 5.0)]
        [Display(Name = "Crop scale")]
        public double LivenessCropScale { get; set; }

        [Range(0, 10)]
        [Display(Name = "Real class index")]
        public int LivenessRealIndex { get; set; }

        [Display(Name = "Output type")]
        public string LivenessOutputType { get; set; }

        [Display(Name = "Normalize")]
        public string LivenessNormalize { get; set; }

        [Display(Name = "Channel order")]
        public string LivenessChannelOrder { get; set; }

        [Range(200, 10000)]
        [Display(Name = "Run timeout (ms)")]
        public int LivenessRunTimeoutMs { get; set; }

        [Range(100, 10000)]
        [Display(Name = "Slow warning (ms)")]
        public int LivenessSlowMs { get; set; }

        [Range(0, 5000)]
        [Display(Name = "Gate wait (ms)")]
        public int LivenessGateWaitMs { get; set; }

        [Range(1, 10)]
        [Display(Name = "Circuit fail streak")]
        public int LivenessCircuitFailStreak { get; set; }

        [Range(0, 300)]
        [Display(Name = "Circuit disable seconds")]
        public int LivenessCircuitDisableSeconds { get; set; }

        // ─── Performance (Phase 3 keys) ────────────────────────────────────────────

        [Range(10, 5000)]
        [Display(Name = "BallTree threshold (employees)")]
        public int BallTreeThreshold { get; set; }

        [Range(4, 64)]
        [Display(Name = "BallTree leaf size")]
        public int BallTreeLeafSize { get; set; }

        [Range(320, 4096)]
        [Display(Name = "Max image dimension (px)")]
        public int MaxImageDimension { get; set; }

        [Range(40, 95)]
        [Display(Name = "Preprocessed JPEG quality")]
        public int PreprocessJpegQuality { get; set; }

        [Display(Name = "Adaptive tolerance tuning")]
        public bool FaceMatchTunerEnabled { get; set; }

        // ─── Location ──────────────────────────────────────────────────────────────

        [Range(5, 5000)]
        [Display(Name = "GPS accuracy required (meters)")]
        public int GPSAccuracyRequired { get; set; }

        [Range(10, 10000)]
        [Display(Name = "Default office radius (meters)")]
        public int GPSRadiusDefault { get; set; }

        [Display(Name = "Fallback office")]
        public int FallbackOfficeId { get; set; }

        // ─── Attendance ────────────────────────────────────────────────────────────

        [Range(1, 600)]
        [Display(Name = "Minimum gap between scans (seconds)")]
        public int MinGapSeconds { get; set; }

        // ─── Review queue ──────────────────────────────────────────────────────────

        [Range(0.50, 0.99)]
        [Display(Name = "Needs review near-match ratio")]
        public double NeedsReviewNearMatchRatio { get; set; }

        [Range(0.00, 0.20)]
        [Display(Name = "Needs review liveness margin")]
        public double NeedsReviewLivenessMargin { get; set; }

        [Range(0, 200)]
        [Display(Name = "Needs review GPS margin (meters)")]
        public int NeedsReviewGpsMargin { get; set; }

        // ─── Visitors ──────────────────────────────────────────────────────────────

        [Range(100, 500000)]
        [Display(Name = "Max visitor log records")]
        public int VisitorMaxRecords { get; set; }

        [Range(1, 20)]
        [Display(Name = "Visitor log retention (years)")]
        public int VisitorRetentionYears { get; set; }

        // ─── UI helpers ────────────────────────────────────────────────────────────

        public List<SelectListItem> OfficeOptions { get; set; } = new List<SelectListItem>();

        public string SavedMessage   { get; set; }
        public string WarningMessage { get; set; }
    }
}
