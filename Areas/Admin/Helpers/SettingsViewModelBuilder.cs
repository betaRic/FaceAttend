using System;
using System.Globalization;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Services;

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
            // Biometrics
            var tolFallback = ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60);
            var tol = ConfigurationService.GetDouble(
                db,
                "Biometrics:DlibTolerance",
                ConfigurationService.GetDouble(db, "DlibTolerance", tolFallback));

            var liveFallback = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75);
            var live = ConfigurationService.GetDouble(db, "Biometrics:LivenessThreshold", liveFallback);

            // Location
            var accFallback = ConfigurationService.GetInt("Location:GPSAccuracyRequired", 50);
            var acc = ConfigurationService.GetInt(db, "Location:GPSAccuracyRequired", accFallback);

            var radFallback = ConfigurationService.GetInt("Location:GPSRadiusDefault", 100);
            var rad = ConfigurationService.GetInt(db, "Location:GPSRadiusDefault", radFallback);

            var fbFallback = ConfigurationService.GetInt("Kiosk:FallbackOfficeId", 0);
            var fb = ConfigurationService.GetInt(db, "Kiosk:FallbackOfficeId", fbFallback);

            // Attendance
            var gapFallback = ConfigurationService.GetInt("Attendance:MinGapSeconds", 10);
            var gap = ConfigurationService.GetInt(db, "Attendance:MinGapSeconds", gapFallback);

            var workStart = ConfigurationService.GetString(
                db,
                "Attendance:WorkStart",
                ConfigurationService.GetString("Attendance:WorkStart", "08:00"));

            var workEnd = ConfigurationService.GetString(
                db,
                "Attendance:WorkEnd",
                ConfigurationService.GetString("Attendance:WorkEnd", "17:00"));

            var lunchStart = ConfigurationService.GetString(
                db,
                "Attendance:LunchStart",
                ConfigurationService.GetString("Attendance:LunchStart", "12:00"));

            var lunchEnd = ConfigurationService.GetString(
                db,
                "Attendance:LunchEnd",
                ConfigurationService.GetString("Attendance:LunchEnd", "13:00"));

            var flexiRequiredHours = ConfigurationService.GetDouble(
                db,
                "Attendance:FlexiRequiredHours",
                ConfigurationService.GetDouble("Attendance:FlexiRequiredHours", 8.0));

            var noGracePeriod = ConfigurationService.GetBool(
                db,
                "Attendance:NoGracePeriod",
                ConfigurationService.GetBool("Attendance:NoGracePeriod", true));

            // Review queue
            var nearMatch = ConfigurationService.GetDouble(db, "NeedsReview:NearMatchRatio", 0.90);
            var liveMargin = ConfigurationService.GetDouble(db, "NeedsReview:LivenessMargin", 0.03);
            var gpsMargin = ConfigurationService.GetInt(db, "NeedsReview:GPSAccuracyMargin", 10);

            // Advanced liveness
            var decision = ConfigurationService.GetString(
                db,
                "Biometrics:Liveness:Decision",
                ConfigurationService.GetString("Biometrics:Liveness:Decision", "max"));

            var scales = ConfigurationService.GetString(
                db,
                "Biometrics:Liveness:MultiCropScales",
                ConfigurationService.GetString("Biometrics:Liveness:MultiCropScales", ""));

            var inputSize = ConfigurationService.GetInt(
                db,
                "Biometrics:LivenessInputSize",
                ConfigurationService.GetInt("Biometrics:LivenessInputSize", 128));

            var cropScale = ConfigurationService.GetDouble(
                db,
                "Biometrics:Liveness:CropScale",
                ConfigurationService.GetDouble("Biometrics:Liveness:CropScale", 2.7));

            var realIndex = ConfigurationService.GetInt(
                db,
                "Biometrics:Liveness:RealIndex",
                ConfigurationService.GetInt("Biometrics:Liveness:RealIndex", 1));

            var outputType = ConfigurationService.GetString(
                db,
                "Biometrics:Liveness:OutputType",
                ConfigurationService.GetString("Biometrics:Liveness:OutputType", "logits"));

            var normalize = ConfigurationService.GetString(
                db,
                "Biometrics:Liveness:Normalize",
                ConfigurationService.GetString("Biometrics:Liveness:Normalize", "0_1"));

            var chanOrder = ConfigurationService.GetString(
                db,
                "Biometrics:Liveness:ChannelOrder",
                ConfigurationService.GetString("Biometrics:Liveness:ChannelOrder", "RGB"));

            var timeoutMs = ConfigurationService.GetInt(
                db,
                "Biometrics:Liveness:RunTimeoutMs",
                ConfigurationService.GetInt("Biometrics:Liveness:RunTimeoutMs", 1500));

            var slowMs = ConfigurationService.GetInt(
                db,
                "Biometrics:Liveness:SlowMs",
                ConfigurationService.GetInt("Biometrics:Liveness:SlowMs", 1200));

            var failStreak = ConfigurationService.GetInt(
                db,
                "Biometrics:Liveness:CircuitFailStreak",
                ConfigurationService.GetInt("Biometrics:Liveness:CircuitFailStreak", 3));

            var disableSec = ConfigurationService.GetInt(
                db,
                "Biometrics:Liveness:CircuitDisableSeconds",
                ConfigurationService.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30));

            // Performance
            var ballTreeTh = ConfigurationService.GetInt(
                db,
                "Biometrics:BallTreeThreshold",
                ConfigurationService.GetInt("Biometrics:BallTreeThreshold", 50));

            var ballTreeLeaf = ConfigurationService.GetInt(
                db,
                "Biometrics:BallTreeLeafSize",
                ConfigurationService.GetInt("Biometrics:BallTreeLeafSize", 16));

            var maxDim = ConfigurationService.GetInt(
                db,
                "Biometrics:MaxImageDimension",
                ConfigurationService.GetInt("Biometrics:MaxImageDimension", 1280));

            var jpegQ = ConfigurationService.GetInt(
                db,
                "Biometrics:PreprocessJpegQuality",
                ConfigurationService.GetInt("Biometrics:PreprocessJpegQuality", 85));

            // Attendance — directional gaps and schedule details
            var inToOut = ConfigurationService.GetInt(db, "Attendance:MinGap:InToOutSeconds",
                ConfigurationService.GetInt("Attendance:MinGap:InToOutSeconds", 1800));
            var outToIn = ConfigurationService.GetInt(db, "Attendance:MinGap:OutToInSeconds",
                ConfigurationService.GetInt("Attendance:MinGap:OutToInSeconds", 300));
            var grace = ConfigurationService.GetInt(db, "Attendance:GraceMinutes",
                ConfigurationService.GetInt("Attendance:GraceMinutes", 10));
            var fullDay = ConfigurationService.GetDouble(db, "Attendance:FullDayHours",
                ConfigurationService.GetDouble("Attendance:FullDayHours", 8.0));
            var halfDay = ConfigurationService.GetDouble(db, "Attendance:HalfDayHours",
                ConfigurationService.GetDouble("Attendance:HalfDayHours", 4.0));
            var lunchDeductAfter = ConfigurationService.GetDouble(db, "Attendance:LunchDeductAfterHours",
                ConfigurationService.GetDouble("Attendance:LunchDeductAfterHours", 5.5));
            var lunchMinutes = ConfigurationService.GetInt(db, "Attendance:LunchMinutes",
                ConfigurationService.GetInt("Attendance:LunchMinutes", 60));

            // Biometrics — scan and enrollment tolerances, pool
            var attendTol = ConfigurationService.GetDouble(db, "Biometrics:AttendanceTolerance",
                ConfigurationService.GetDouble("Biometrics:AttendanceTolerance", 0.65));
            var strictTol = ConfigurationService.GetDouble(db, "Biometrics:EnrollmentStrictTolerance",
                ConfigurationService.GetDouble("Biometrics:EnrollmentStrictTolerance", 0.45));
            var dlibPool = ConfigurationService.GetInt(db, "Biometrics:DlibPoolSize",
                ConfigurationService.GetInt("Biometrics:DlibPoolSize", 4));
            var maxScans = ConfigurationService.GetInt(db, "Kiosk:MaxConcurrentScans",
                ConfigurationService.GetInt("Kiosk:MaxConcurrentScans", 4));
            var enrollTarget = ConfigurationService.GetInt(db, "Biometrics:Enroll:CaptureTarget",
                ConfigurationService.GetInt("Biometrics:Enroll:CaptureTarget", 8));
            var enrollMaxVec = ConfigurationService.GetInt(db, "Biometrics:Enroll:MaxStoredVectors",
                ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 5));
            var visitorTol = ConfigurationService.GetDouble(db, "Visitors:DlibTolerance",
                ConfigurationService.GetDouble("Visitors:DlibTolerance", attendTol));
            var sharpDesktop = ConfigurationService.GetDouble(db, "Biometrics:Enroll:SharpnessThreshold",
                ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold", 80.0));
            var sharpMobile = ConfigurationService.GetDouble(db, "Biometrics:Enroll:SharpnessThreshold:Mobile",
                ConfigurationService.GetDouble("Biometrics:Enroll:SharpnessThreshold:Mobile", 50.0));

            // Visitors
            var visitorEnabled = ConfigurationService.GetBool(
                db,
                "Kiosk:VisitorEnabled",
                ConfigurationService.GetBool("Kiosk:VisitorEnabled", true));

            var visMaxRec = ConfigurationService.GetInt(
                db,
                "Visitors:MaxRecords",
                ConfigurationService.GetInt("Visitors:MaxRecords", 10000));

            var visRetYears = ConfigurationService.GetInt(
                db,
                "Visitors:RetentionYears",
                ConfigurationService.GetInt("Visitors:RetentionYears", 2));

            var vm = new SettingsVm
            {
                // Biometrics
                DlibTolerance = tol,
                LivenessThreshold = live,

                // Advanced liveness
                LivenessDecision = NormalizeOrDefault(decision, "max"),
                LivenessMultiCropScales = (scales ?? "").Trim(),
                LivenessInputSize = inputSize,
                LivenessCropScale = cropScale,
                LivenessRealIndex = realIndex,
                LivenessOutputType = NormalizeOrDefault(outputType, "logits"),
                LivenessNormalize = NormalizeOrDefault(normalize, "0_1"),
                LivenessChannelOrder = NormalizeOrDefault(chanOrder, "RGB"),
                LivenessRunTimeoutMs = timeoutMs,
                LivenessSlowMs = slowMs,
                LivenessCircuitFailStreak = failStreak,
                LivenessCircuitDisableSeconds = disableSec,

                // Performance
                BallTreeThreshold = ballTreeTh,
                BallTreeLeafSize = ballTreeLeaf,
                MaxImageDimension = maxDim,
                PreprocessJpegQuality = jpegQ,

                // Location
                GPSAccuracyRequired = acc,
                GPSRadiusDefault = rad,
                FallbackOfficeId = fb,

                // Attendance
                MinGapSeconds = gap,
                WorkStart = NormalizeTimeOrDefault(workStart, "08:00"),
                WorkEnd = NormalizeTimeOrDefault(workEnd, "17:00"),
                LunchStart = NormalizeTimeOrDefault(lunchStart, "12:00"),
                LunchEnd = NormalizeTimeOrDefault(lunchEnd, "13:00"),
                FlexiRequiredHours = flexiRequiredHours,
                NoGracePeriod = noGracePeriod,

                // Attendance directional gaps
                MinGapInToOutSeconds = inToOut,
                MinGapOutToInSeconds = outToIn,
                GraceMinutes = grace,
                FullDayHours = fullDay,
                HalfDayHours = halfDay,
                LunchDeductAfterHours = lunchDeductAfter,
                LunchMinutes = lunchMinutes,

                // Biometrics extended
                AttendanceTolerance = attendTol,
                EnrollmentStrictTolerance = strictTol,
                DlibPoolSize = dlibPool,
                MaxConcurrentScans = maxScans,
                EnrollCaptureTarget = enrollTarget,
                EnrollMaxStoredVectors = enrollMaxVec,
                VisitorDlibTolerance = visitorTol,
                EnrollSharpnessThreshold = sharpDesktop,
                EnrollSharpnessThresholdMobile = sharpMobile,

                // Review queue
                NeedsReviewNearMatchRatio = nearMatch,
                NeedsReviewLivenessMargin = liveMargin,
                NeedsReviewGpsMargin = gpsMargin,

                // Visitors
                VisitorEnabled = visitorEnabled,
                VisitorMaxRecords = visMaxRec,
                VisitorRetentionYears = visRetYears,

                OfficeOptions = AdminQueryHelper.BuildOfficeOptionsWithAuto(db, fb)
            };

            // Check for legacy keys and add warnings
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

                DlibTolerance = ConfigurationService.GetDouble("Biometrics:DlibTolerance", 0.60),
                LivenessThreshold = ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75),

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

                LivenessDecision = NormalizeOrDefault(
                    ConfigurationService.GetString("Biometrics:Liveness:Decision", "max"),
                    "max"),
                LivenessMultiCropScales = ConfigurationService.GetString("Biometrics:Liveness:MultiCropScales", ""),
                LivenessOutputType = NormalizeOrDefault(
                    ConfigurationService.GetString("Biometrics:Liveness:OutputType", "logits"),
                    "logits"),
                LivenessNormalize = NormalizeOrDefault(
                    ConfigurationService.GetString("Biometrics:Liveness:Normalize", "0_1"),
                    "0_1"),
                LivenessChannelOrder = NormalizeOrDefault(
                    ConfigurationService.GetString("Biometrics:Liveness:ChannelOrder", "RGB"),
                    "RGB"),
                LivenessInputSize = ConfigurationService.GetInt("Biometrics:LivenessInputSize", 128),
                LivenessCropScale = ConfigurationService.GetDouble("Biometrics:Liveness:CropScale", 2.7),
                LivenessRealIndex = ConfigurationService.GetInt("Biometrics:Liveness:RealIndex", 1),
                LivenessRunTimeoutMs = ConfigurationService.GetInt("Biometrics:Liveness:RunTimeoutMs", 1500),
                LivenessSlowMs = ConfigurationService.GetInt("Biometrics:Liveness:SlowMs", 1200),
                LivenessCircuitFailStreak = ConfigurationService.GetInt("Biometrics:Liveness:CircuitFailStreak", 3),
                LivenessCircuitDisableSeconds = ConfigurationService.GetInt("Biometrics:Liveness:CircuitDisableSeconds", 30),

                VisitorEnabled = ConfigurationService.GetBool("Kiosk:VisitorEnabled", true),
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

            if (ConfigurationService.HasKey(db, "DlibTolerance") &&
                !ConfigurationService.HasKey(db, "Biometrics:DlibTolerance"))
            {
                messages.Add("Legacy key DlibTolerance exists in SystemConfiguration. " +
                    "New key Biometrics:DlibTolerance is preferred. Save settings once to migrate.");
            }

            if (ConfigurationService.HasKey(db, "Biometrics:Liveness:GateWaitMs"))
            {
                messages.Add("Removed key Biometrics:Liveness:GateWaitMs still exists in SystemConfiguration. Save settings once to clean it up.");
            }

            return string.Join(" ", messages);
        }

        #region Helper Methods

        private static string NormalizeTimeOrDefault(string value, string fallback)
        {
            TimeSpan time;
            return TryParseTime(value, out time)
                ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
                : fallback;
        }

        private static bool TryParseTime(string value, out TimeSpan result)
        {
            result = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();

            return
                TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out result) ||
                TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out result) ||
                TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out result);
        }

        private static string NormalizeOrDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        #endregion
    }
}
