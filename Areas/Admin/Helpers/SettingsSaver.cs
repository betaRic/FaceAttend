using System;
using System.Globalization;
using System.Web;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Services;

namespace FaceAttend.Areas.Admin.Helpers
{
    /// <summary>
    /// Persists settings to the database.
    /// Extracted from SettingsController to reduce controller size.
    /// </summary>
    public static class SettingsSaver
    {
        /// <summary>
        /// Saves all settings from the view model to the database.
        /// </summary>
        public static void SaveSettings(
            FaceAttendDBEntities db,
            SettingsVm vm,
            TimeSpan workStartTs,
            TimeSpan workEndTs,
            TimeSpan lunchStartTs,
            TimeSpan lunchEndTs,
            string modifiedBy)
        {
            // ── Biometrics ────────────────────────────────────────────────────

            ConfigurationService.Upsert(
                db,
                "Biometrics:DlibTolerance",
                vm.DlibTolerance.ToString(CultureInfo.InvariantCulture),
                "double",
                "Dlib face distance tolerance. Lower = stricter match.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:LivenessThreshold",
                vm.LivenessThreshold.ToString(CultureInfo.InvariantCulture),
                "double",
                "Minimum liveness score to accept a scan.",
                modifiedBy);

            // ── Location ──────────────────────────────────────────────────────

            ConfigurationService.Upsert(
                db,
                "Location:GPSAccuracyRequired",
                vm.GPSAccuracyRequired.ToString(CultureInfo.InvariantCulture),
                "int",
                "Max GPS accuracy (meters). Higher means less strict.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Location:GPSRadiusDefault",
                vm.GPSRadiusDefault.ToString(CultureInfo.InvariantCulture),
                "int",
                "Default office geofence radius (meters) when office radius is not set.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Kiosk:FallbackOfficeId",
                vm.FallbackOfficeId.ToString(CultureInfo.InvariantCulture),
                "int",
                "Office used for desktop kiosks when GPS is skipped. 0 = first active office.",
                modifiedBy);

            // ── Attendance ────────────────────────────────────────────────────

            ConfigurationService.Upsert(
                db,
                "Attendance:MinGapSeconds",
                vm.MinGapSeconds.ToString(CultureInfo.InvariantCulture),
                "int",
                "Minimum seconds between scans to prevent double taps.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Attendance:WorkStart",
                workStartTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                "string",
                "Official work start time in HH:mm.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Attendance:WorkEnd",
                workEndTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                "string",
                "Official work end time in HH:mm.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Attendance:LunchStart",
                string.IsNullOrWhiteSpace(vm.LunchStart)
                    ? ""
                    : lunchStartTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                "string",
                "Lunch break start time in HH:mm. Blank means no lunch window.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Attendance:LunchEnd",
                string.IsNullOrWhiteSpace(vm.LunchEnd)
                    ? ""
                    : lunchEndTs.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                "string",
                "Lunch break end time in HH:mm. Blank means no lunch window.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Attendance:FlexiRequiredHours",
                vm.FlexiRequiredHours.ToString(CultureInfo.InvariantCulture),
                "double",
                "Required work hours for flexi employees.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Attendance:NoGracePeriod",
                vm.NoGracePeriod ? "true" : "false",
                "bool",
                "If true, any time after work start is late.",
                modifiedBy);

            ConfigurationService.Upsert(db, "Attendance:MinGap:InToOutSeconds",
                vm.MinGapInToOutSeconds.ToString(CultureInfo.InvariantCulture), "int",
                "Minimum seconds required between Time-In and next Time-Out.", modifiedBy);

            ConfigurationService.Upsert(db, "Attendance:MinGap:OutToInSeconds",
                vm.MinGapOutToInSeconds.ToString(CultureInfo.InvariantCulture), "int",
                "Minimum seconds required between Time-Out and next Time-In.", modifiedBy);

            ConfigurationService.Upsert(db, "Attendance:GraceMinutes",
                vm.GraceMinutes.ToString(CultureInfo.InvariantCulture), "int",
                "Minutes after WorkStart before a scan is considered late.", modifiedBy);

            ConfigurationService.Upsert(db, "Attendance:FullDayHours",
                vm.FullDayHours.ToString(CultureInfo.InvariantCulture), "double",
                "Required hours for a full-day attendance record.", modifiedBy);

            ConfigurationService.Upsert(db, "Attendance:HalfDayHours",
                vm.HalfDayHours.ToString(CultureInfo.InvariantCulture), "double",
                "Hours threshold for a half-day attendance record.", modifiedBy);

            ConfigurationService.Upsert(db, "Attendance:LunchDeductAfterHours",
                vm.LunchDeductAfterHours.ToString(CultureInfo.InvariantCulture), "double",
                "Deduct lunch minutes only when total hours exceed this value.", modifiedBy);

            ConfigurationService.Upsert(db, "Attendance:LunchMinutes",
                vm.LunchMinutes.ToString(CultureInfo.InvariantCulture), "int",
                "Minutes to deduct for lunch break.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:AttendanceTolerance",
                vm.AttendanceTolerance.ToString(CultureInfo.InvariantCulture), "double",
                "Face distance tolerance for attendance scans. Higher = more lenient.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:EnrollmentStrictTolerance",
                vm.EnrollmentStrictTolerance.ToString(CultureInfo.InvariantCulture), "double",
                "Strict tolerance for enrollment duplicate detection. Lower = harder to enroll same face twice.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:DlibPoolSize",
                vm.DlibPoolSize.ToString(CultureInfo.InvariantCulture), "int",
                "Number of Dlib instances in the pool. Requires app restart to take effect.", modifiedBy);

            ConfigurationService.Upsert(db, "Kiosk:MaxConcurrentScans",
                vm.MaxConcurrentScans.ToString(CultureInfo.InvariantCulture), "int",
                "Maximum simultaneous scan requests before 503 is returned.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:CaptureTarget",
                vm.EnrollCaptureTarget.ToString(CultureInfo.InvariantCulture), "int",
                "Target number of frames to collect during enrollment.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:MaxStoredVectors",
                vm.EnrollMaxStoredVectors.ToString(CultureInfo.InvariantCulture), "int",
                "Maximum face vectors stored per enrolled employee.", modifiedBy);

            ConfigurationService.Upsert(db, "Visitors:DlibTolerance",
                vm.VisitorDlibTolerance.ToString(CultureInfo.InvariantCulture), "double",
                "Face distance tolerance for visitor recognition at kiosk.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:SharpnessThreshold",
                vm.EnrollSharpnessThreshold.ToString(CultureInfo.InvariantCulture), "double",
                "Minimum Laplacian variance score for enrollment frames on desktop.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:SharpnessThreshold:Mobile",
                vm.EnrollSharpnessThresholdMobile.ToString(CultureInfo.InvariantCulture), "double",
                "Minimum Laplacian variance score for enrollment frames on mobile.", modifiedBy);

            // ── Review queue ──────────────────────────────────────────────────

            ConfigurationService.Upsert(
                db,
                "NeedsReview:NearMatchRatio",
                vm.NeedsReviewNearMatchRatio.ToString(CultureInfo.InvariantCulture),
                "double",
                "If distance is within this ratio of the threshold, mark record as NeedsReview.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "NeedsReview:LivenessMargin",
                vm.NeedsReviewLivenessMargin.ToString(CultureInfo.InvariantCulture),
                "double",
                "If liveness is within this margin above threshold, mark record as NeedsReview.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "NeedsReview:GPSAccuracyMargin",
                vm.NeedsReviewGpsMargin.ToString(CultureInfo.InvariantCulture),
                "int",
                "If GPS accuracy is within this margin of the required limit, mark record as NeedsReview.",
                modifiedBy);

            // ── Advanced liveness ─────────────────────────────────────────────

            var decision = NormalizeOrDefault(vm.LivenessDecision, "max");
            var scales = (vm.LivenessMultiCropScales ?? "").Trim();
            var outputType = NormalizeOrDefault(vm.LivenessOutputType, "logits");
            var normalize = NormalizeOrDefault(vm.LivenessNormalize, "0_1");
            var channelOrder = NormalizeOrDefault(vm.LivenessChannelOrder, "RGB");

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:Decision",
                decision,
                "string",
                "How to combine multi-crop liveness results: max or avg.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:MultiCropScales",
                scales,
                "string",
                "Comma-separated crop scales for liveness tuning (example: 2.3,2.7,3.1).",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:LivenessInputSize",
                vm.LivenessInputSize.ToString(CultureInfo.InvariantCulture),
                "int",
                "Liveness model input size (pixels). Must match the model.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:CropScale",
                vm.LivenessCropScale.ToString(CultureInfo.InvariantCulture),
                "double",
                "Crop scale around detected face before liveness inference.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:RealIndex",
                vm.LivenessRealIndex.ToString(CultureInfo.InvariantCulture),
                "int",
                "Index of the REAL class in model output.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:OutputType",
                outputType,
                "string",
                "Model output type: logits or probs.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:Normalize",
                normalize,
                "string",
                "Input normalize mode: 0_1, minus1_1, imagenet, none.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:ChannelOrder",
                channelOrder,
                "string",
                "Channel order for tensor: RGB or BGR.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:RunTimeoutMs",
                vm.LivenessRunTimeoutMs.ToString(CultureInfo.InvariantCulture),
                "int",
                "Max ONNX run time before the circuit breaker trips.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:SlowMs",
                vm.LivenessSlowMs.ToString(CultureInfo.InvariantCulture),
                "int",
                "Milliseconds considered slow for liveness inference.",
                modifiedBy);

            // Clean up removed / legacy keys while saving the current settings set.
            ConfigurationService.Delete(db, "Biometrics:Liveness:GateWaitMs");
            ConfigurationService.Delete(db, "Biometrics:FaceMatchTuner:Enabled");
            ConfigurationService.Delete(db, "Biometrics:FaceMatchTunerEnabled");

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:CircuitFailStreak",
                vm.LivenessCircuitFailStreak.ToString(CultureInfo.InvariantCulture),
                "int",
                "How many failures before circuit opens.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:Liveness:CircuitDisableSeconds",
                vm.LivenessCircuitDisableSeconds.ToString(CultureInfo.InvariantCulture),
                "int",
                "How long to disable liveness after failures.",
                modifiedBy);

            // ── Performance ───────────────────────────────────────────────────

            ConfigurationService.Upsert(
                db,
                "Biometrics:BallTreeThreshold",
                vm.BallTreeThreshold.ToString(CultureInfo.InvariantCulture),
                "int",
                "Build BallTree face index when enrolled employee count >= this value.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:BallTreeLeafSize",
                vm.BallTreeLeafSize.ToString(CultureInfo.InvariantCulture),
                "int",
                "BallTree leaf size. Default 16. Range 4–64.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:MaxImageDimension",
                vm.MaxImageDimension.ToString(CultureInfo.InvariantCulture),
                "int",
                "Resize images larger than this on either axis before face detection.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:PreprocessJpegQuality",
                vm.PreprocessJpegQuality.ToString(CultureInfo.InvariantCulture),
                "int",
                "JPEG quality of the resized temp image. Range 40–95.",
                modifiedBy);

            // ── Visitors ──────────────────────────────────────────────────────

            ConfigurationService.Upsert(
                db,
                "Visitors:MaxRecords",
                vm.VisitorMaxRecords.ToString(CultureInfo.InvariantCulture),
                "int",
                "Max visitor log records to retain (soft cap).",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Visitors:RetentionYears",
                vm.VisitorRetentionYears.ToString(CultureInfo.InvariantCulture),
                "int",
                "Delete visitor logs older than this many years when cleanup is run.",
                modifiedBy);
        }

        /// <summary>
        /// Creates audit log entry for settings save.
        /// </summary>
        public static void LogSettingsChange(
            FaceAttendDBEntities db,
            HttpRequestBase request,
            SettingsVm vm,
            string savedBy)
        {
            AuditHelper.Log(
                db,
                request,
                AuditHelper.ActionSettingChange,
                "SystemConfiguration",
                "bulk-save",
                "Nag-save ng admin settings.",
                null,
                new
                {
                    vm.DlibTolerance,
                    vm.LivenessThreshold,
                    vm.GPSAccuracyRequired,
                    vm.GPSRadiusDefault,
                    vm.MinGapSeconds,
                    vm.NeedsReviewNearMatchRatio,
                    vm.NeedsReviewLivenessMargin,
                    vm.NeedsReviewGpsMargin,
                    vm.VisitorMaxRecords,
                    vm.VisitorRetentionYears,
                    savedBy
                });
        }

        #region Private Helpers

        private static string NormalizeOrDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        #endregion
    }
}
