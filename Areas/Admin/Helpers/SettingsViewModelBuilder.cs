using System;
using System.Globalization;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Services;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Areas.Admin.Helpers
{
    /// <summary>
    /// Builds SettingsVm from database configuration.
    /// Extracted from SettingsController to reduce controller size.
    /// </summary>
    public static class SettingsViewModelBuilder
    {
        /// <summary>
        /// Builds the settings view model from database values.
        /// </summary>
        public static SettingsVm BuildVm(FaceAttendDBEntities db)
        {
            var attendTol = D(db, "Biometrics:AttendanceTolerance", 0.65);
            var fb        = I(db, "Kiosk:FallbackOfficeId", 0);

            var vm = new SettingsVm
            {
                // Biometrics
                RecognitionTolerance             = GetRecognitionToleranceWithLegacyFallback(db),
                AntiSpoofThreshold         = D(db, "Biometrics:AntiSpoof:ClearThreshold", 0.45),
                AttendanceTolerance       = attendTol,
                EnrollmentStrictTolerance = D(db, "Biometrics:EnrollmentStrictTolerance", 0.45),
                WorkerAnalyzeTimeoutMs              = I(db, "Biometrics:Worker:AnalyzeTimeoutMs", 5000),
                MaxConcurrentScans        = I(db, "Kiosk:MaxConcurrentScans", 4),
                EnrollCaptureTarget       = I(db, "Biometrics:Enroll:CaptureTarget", 8),
                EnrollMaxStoredVectors    = I(db, "Biometrics:Enroll:MaxStoredVectors", 8),
                VisitorRecognitionTolerance      = D(db, "Visitors:RecognitionTolerance", attendTol),
                EnrollSharpnessThreshold       = D(db, "Biometrics:Enroll:SharpnessThreshold",        35.0),
                EnrollSharpnessThresholdMobile = D(db, "Biometrics:Enroll:SharpnessThreshold:Mobile", 28.0),

                // Performance
                BallTreeThreshold     = I(db, "Biometrics:BallTreeThreshold",      50),
                BallTreeLeafSize      = I(db, "Biometrics:BallTreeLeafSize",        16),
                MaxImageDimension     = I(db, "Biometrics:MaxImageDimension",     1280),
                PreprocessJpegQuality = I(db, "Biometrics:PreprocessJpegQuality",   85),

                // Location
                GPSAccuracyRequired = I(db, "Location:GPSAccuracyRequired", 50),
                GPSRadiusDefault    = I(db, "Location:GPSRadiusDefault",   100),
                FallbackOfficeId    = fb,

                // Attendance
                MinGapSeconds     = I(db, "Attendance:MinGapSeconds", 10),
                WorkStart         = NormalizeTimeOrDefault(S(db, "Attendance:WorkStart",  "08:00"), "08:00"),
                WorkEnd           = NormalizeTimeOrDefault(S(db, "Attendance:WorkEnd",    "17:00"), "17:00"),
                LunchStart        = NormalizeTimeOrDefault(S(db, "Attendance:LunchStart", "12:00"), "12:00"),
                LunchEnd          = NormalizeTimeOrDefault(S(db, "Attendance:LunchEnd",   "13:00"), "13:00"),
                FlexiRequiredHours    = D(db, "Attendance:FlexiRequiredHours",    8.0),
                NoGracePeriod         = B(db, "Attendance:NoGracePeriod",         true),
                MinGapInToOutSeconds  = I(db, "Attendance:MinGap:InToOutSeconds", 1800),
                MinGapOutToInSeconds  = I(db, "Attendance:MinGap:OutToInSeconds", 300),
                GraceMinutes          = I(db, "Attendance:GraceMinutes",          10),
                FullDayHours          = D(db, "Attendance:FullDayHours",          8.0),
                HalfDayHours          = D(db, "Attendance:HalfDayHours",          4.0),
                LunchDeductAfterHours = D(db, "Attendance:LunchDeductAfterHours", 5.5),
                LunchMinutes          = I(db, "Attendance:LunchMinutes",          60),

                // Review queue
                NeedsReviewNearMatchRatio = D(db, "NeedsReview:NearMatchRatio",    0.90),
                NeedsReviewAntiSpoofMargin = D(db, "NeedsReview:AntiSpoofMargin", 0.03),
                NeedsReviewGpsMargin      = I(db, "NeedsReview:GPSAccuracyMargin", 10),

                // Visitors
                VisitorEnabled      = B(db, "Kiosk:VisitorEnabled",      false),
                VisitorMaxRecords   = I(db, "Visitors:MaxRecords",     10000),
                VisitorRetentionYears = I(db, "Visitors:RetentionYears", 2),

                OfficeOptions = AdminQueryHelper.BuildOfficeOptionsWithAuto(db, fb)
            };

            vm.WarningMessage = BuildWarningMessages(db);
            return vm;
        }

        /// <summary>
        /// Builds the settings view model with safe defaults (when DB is unavailable).
        /// </summary>
        public static SettingsVm BuildSafeVm(string errorMessage)
        {
            return new SettingsVm
            {
                WarningMessage =
                    "Could not load settings from the database: " + errorMessage +
                    " — Defaults are shown below. Save to persist them.",

                RecognitionTolerance = ConfigurationService.GetDouble("Biometrics:RecognitionTolerance", 0.60),
                WorkerAnalyzeTimeoutMs = ConfigurationService.GetInt("Biometrics:Worker:AnalyzeTimeoutMs", 5000),
                AntiSpoofThreshold = ConfigurationService.GetDouble("Biometrics:AntiSpoof:ClearThreshold", 0.45),

                GPSAccuracyRequired = ConfigurationService.GetInt("Location:GPSAccuracyRequired", 50),
                GPSRadiusDefault = ConfigurationService.GetInt("Location:GPSRadiusDefault", 100),
                FallbackOfficeId = ConfigurationService.GetInt("Kiosk:FallbackOfficeId", 0),

                MinGapSeconds = ConfigurationService.GetInt("Attendance:MinGapSeconds", 10),
                WorkStart = NormalizeTimeOrDefault(ConfigurationService.GetString("Attendance:WorkStart", "08:00"), "08:00"),
                WorkEnd = NormalizeTimeOrDefault(ConfigurationService.GetString("Attendance:WorkEnd", "17:00"), "17:00"),
                LunchStart = NormalizeTimeOrDefault(ConfigurationService.GetString("Attendance:LunchStart", "12:00"), "12:00"),
                LunchEnd = NormalizeTimeOrDefault(ConfigurationService.GetString("Attendance:LunchEnd", "13:00"), "13:00"),
                FlexiRequiredHours = ConfigurationService.GetDouble("Attendance:FlexiRequiredHours", 8.0),
                NoGracePeriod = ConfigurationService.GetBool("Attendance:NoGracePeriod", true),
                FullDayHours = ConfigurationService.GetDouble("Attendance:FullDayHours", 8.0),
                HalfDayHours = ConfigurationService.GetDouble("Attendance:HalfDayHours", 4.0),
                LunchDeductAfterHours = ConfigurationService.GetDouble("Attendance:LunchDeductAfterHours", 5.5),
                LunchMinutes = ConfigurationService.GetInt("Attendance:LunchMinutes", 60),
                MinGapInToOutSeconds = ConfigurationService.GetInt("Attendance:MinGap:InToOutSeconds", 1800),
                MinGapOutToInSeconds = ConfigurationService.GetInt("Attendance:MinGap:OutToInSeconds", 300),
                GraceMinutes = ConfigurationService.GetInt("Attendance:GraceMinutes", 10),

                BallTreeThreshold = ConfigurationService.GetInt("Biometrics:BallTreeThreshold", 50),
                BallTreeLeafSize = ConfigurationService.GetInt("Biometrics:BallTreeLeafSize", 16),
                MaxImageDimension = ConfigurationService.GetInt("Biometrics:MaxImageDimension", 1280),
                PreprocessJpegQuality = ConfigurationService.GetInt("Biometrics:PreprocessJpegQuality", 85),

                VisitorEnabled = ConfigurationService.GetBool("Kiosk:VisitorEnabled", false),
                VisitorMaxRecords = ConfigurationService.GetInt("Visitors:MaxRecords", 10000),
                VisitorRetentionYears = ConfigurationService.GetInt("Visitors:RetentionYears", 2),

                OfficeOptions = new System.Collections.Generic.List<SelectListItem>
                {
                    new SelectListItem
                    {
                        Text = "Auto (first active office)",
                        Value = "0",
                        Selected = true
                    }
                }
            };
        }

        /// <summary>
        /// Builds warning messages for legacy configuration keys.
        /// </summary>
        private static string BuildWarningMessages(FaceAttendDBEntities db)
        {
            var messages = new System.Collections.Generic.List<string>();

            if (ConfigurationService.HasKey(db, "RecognitionTolerance") &&
                !ConfigurationService.HasKey(db, "Biometrics:RecognitionTolerance"))
            {
                messages.Add("Legacy key RecognitionTolerance exists in SystemConfiguration. " +
                    "New key Biometrics:RecognitionTolerance is preferred. Save settings once to migrate.");
            }

            if (ConfigurationService.HasKey(db, "Biometrics:AntiSpoof:GateWaitMs"))
            {
                messages.Add("Removed key Biometrics:AntiSpoof:GateWaitMs still exists in SystemConfiguration. Save settings once to clean it up.");
            }

            return string.Join(" ", messages);
        }

        #region Helper Methods

        // ── Short-hand DB readers (DB value with Web.config fallback) ─────────
        // Each reads from DB first; if not found, falls back to Web.config; if
        // not found there either, uses the hard-coded default.

        private static double D(FaceAttendDBEntities db, string key, double def)
            => ConfigurationService.GetDouble(db, key, ConfigurationService.GetDouble(key, def));

        private static int I(FaceAttendDBEntities db, string key, int def)
            => ConfigurationService.GetInt(db, key, ConfigurationService.GetInt(key, def));

        private static string S(FaceAttendDBEntities db, string key, string def)
            => ConfigurationService.GetString(db, key, ConfigurationService.GetString(key, def));

        private static bool B(FaceAttendDBEntities db, string key, bool def)
            => ConfigurationService.GetBool(db, key, ConfigurationService.GetBool(key, def));

        // Special case: RecognitionTolerance has a legacy key in the DB as an intermediate fallback.
        private static double GetRecognitionToleranceWithLegacyFallback(FaceAttendDBEntities db)
        {
            var webConfigDefault = ConfigurationService.GetDouble("Biometrics:RecognitionTolerance", 0.60);
            var legacyDbValue    = ConfigurationService.GetDouble(db, "RecognitionTolerance", webConfigDefault);
            return ConfigurationService.GetDouble(db, "Biometrics:RecognitionTolerance", legacyDbValue);
        }

        private static string NormalizeTimeOrDefault(string value, string fallback)
        {
            TimeSpan time;
            return TimeHelper.TryParseTime(value, out time)
                ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
                : fallback;
        }

        #endregion
    }
}
