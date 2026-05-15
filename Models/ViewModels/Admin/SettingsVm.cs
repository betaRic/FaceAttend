using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Web.Mvc;

namespace FaceAttend.Models.ViewModels.Admin
{
    public class SettingsVm
    {
        // ─── Biometrics ────────────────────────────────────────────────────────────

        [Range(0.20, 1.00)]
        [Display(Name = "Face match tolerance")]
        public double RecognitionTolerance { get; set; }

        [Range(0.00, 1.00)]
        [Display(Name = "Anti-spoof clear threshold")]
        public double AntiSpoofThreshold { get; set; }

        [Range(0.20, 1.00)]
        [Display(Name = "Attendance match tolerance")]
        public double AttendanceTolerance { get; set; }

        [Range(0.20, 0.70)]
        [Display(Name = "Enrollment duplicate check tolerance")]
        public double EnrollmentStrictTolerance { get; set; }

        [Range(500, 30000)]
        [Display(Name = "Engine analyze timeout (ms)")]
        public int EngineAnalyzeTimeoutMs { get; set; }

        [Display(Name = "Model hashes")]
        [DataType(DataType.MultilineText)]
        public string BiometricModelHashes { get; set; }

        [Range(1, 32)]
        [Display(Name = "Max concurrent scans")]
        public int MaxConcurrentScans { get; set; }

        [Range(5, 25)]
        [Display(Name = "Enrollment capture target (frames)")]
        public int EnrollCaptureTarget { get; set; }

        [Range(1, 25)]
        [Display(Name = "Enrollment stored vectors per employee")]
        public int EnrollMaxStoredVectors { get; set; }

        [Range(0.0, 1.00)]
        [Display(Name = "Visitor match tolerance")]
        public double VisitorRecognitionTolerance { get; set; }

        [Range(10, 150)]
        [Display(Name = "Enrollment sharpness threshold (desktop)")]
        public double EnrollSharpnessThreshold { get; set; }

        [Range(10, 100)]
        [Display(Name = "Enrollment sharpness threshold (mobile)")]
        public double EnrollSharpnessThresholdMobile { get; set; }

        // ─── Performance ───────────────────────────────────────────────────────────

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
        [Display(Name = "Anti double-tap gap (seconds)")]
        public int MinGapSeconds { get; set; }

        [Range(300, 14400)]
        [Display(Name = "Minimum time IN → OUT (seconds)")]
        public int MinGapInToOutSeconds { get; set; }

        [Range(60, 3600)]
        [Display(Name = "Minimum time OUT → IN (seconds)")]
        public int MinGapOutToInSeconds { get; set; }

        [Range(0, 60)]
        [Display(Name = "Grace period (minutes)")]
        public int GraceMinutes { get; set; }

        [Range(1.0, 24.0)]
        [Display(Name = "Full day hours")]
        public double FullDayHours { get; set; }

        [Range(0.5, 12.0)]
        [Display(Name = "Half day hours")]
        public double HalfDayHours { get; set; }

        [Range(0.0, 12.0)]
        [Display(Name = "Lunch deduct after (hours)")]
        public double LunchDeductAfterHours { get; set; }

        [Range(0, 120)]
        [Display(Name = "Lunch deduct minutes")]
        public int LunchMinutes { get; set; }

        [Display(Name = "Work start")]
        public string WorkStart { get; set; }

        [Display(Name = "Work end")]
        public string WorkEnd { get; set; }

        [Display(Name = "Lunch start")]
        public string LunchStart { get; set; }

        [Display(Name = "Lunch end")]
        public string LunchEnd { get; set; }

        [Range(1.0, 12.0)]
        [Display(Name = "Flexi required hours")]
        public double FlexiRequiredHours { get; set; }

        [Display(Name = "No grace period")]
        public bool NoGracePeriod { get; set; }

        // ─── Review queue ──────────────────────────────────────────────────────────

        [Range(0.50, 0.99)]
        [Display(Name = "Needs review near-match ratio")]
        public double NeedsReviewNearMatchRatio { get; set; }

        [Range(0.00, 0.20)]
        [Display(Name = "Needs review anti-spoof margin")]
        public double NeedsReviewAntiSpoofMargin { get; set; }

        [Range(0, 200)]
        [Display(Name = "Needs review GPS margin (meters)")]
        public int NeedsReviewGpsMargin { get; set; }

        // ─── Visitors ──────────────────────────────────────────────────────────────

        [Display(Name = "Enable visitor sign-in")]
        public bool VisitorEnabled { get; set; }

        [Range(100, 500000)]
        [Display(Name = "Max visitor log records")]
        public int VisitorMaxRecords { get; set; }

        [Range(1, 20)]
        [Display(Name = "Visitor log retention (years)")]
        public int VisitorRetentionYears { get; set; }

        // ─── UI helpers ────────────────────────────────────────────────────────────

        public List<SelectListItem> OfficeOptions { get; set; } = new List<SelectListItem>();

        public string SavedMessage { get; set; }
        public string WarningMessage { get; set; }

        // ─── Diagnostics (read-only, not saved) ───────────────────────────────────

        public string FaceCacheStats { get; set; }

        // ─── TOTP 2FA (read-only, not saved via this form) ─────────────────────────

        public bool TotpEnabled { get; set; }
        public bool TotpConfigured { get; set; }

        public bool AdminPinStoredInDatabase { get; set; }
        public bool AdminPinUsingLegacyEnvironmentFallback { get; set; }
    }
}
