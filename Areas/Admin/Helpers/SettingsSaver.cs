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
            SaveBiometricSettings(db, vm, modifiedBy);
            SaveLocationSettings(db, vm, modifiedBy);
            SaveAttendanceSettings(db, vm, workStartTs, workEndTs, lunchStartTs, lunchEndTs, modifiedBy);
            SaveReviewQueueSettings(db, vm, modifiedBy);
            CleanupLegacyKeys(db);
            SavePerformanceSettings(db, vm, modifiedBy);
            SaveVisitorSettings(db, vm, modifiedBy);
        }

        private static void SaveBiometricSettings(FaceAttendDBEntities db, SettingsVm vm, string modifiedBy)
        {
            ConfigurationService.Upsert(
                db,
                "Biometrics:RecognitionTolerance",
                vm.RecognitionTolerance.ToString(CultureInfo.InvariantCulture),
                "double",
                "Face embedding distance tolerance. Lower = stricter match.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "Biometrics:AntiSpoof:ClearThreshold",
                vm.AntiSpoofThreshold.ToString(CultureInfo.InvariantCulture),
                "double",
                "Minimum anti-spoof score to accept a scan.",
                modifiedBy);

        }

        private static void SaveLocationSettings(FaceAttendDBEntities db, SettingsVm vm, string modifiedBy)
        {
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

        }

        private static void SaveAttendanceSettings(
            FaceAttendDBEntities db, SettingsVm vm,
            TimeSpan workStartTs, TimeSpan workEndTs,
            TimeSpan lunchStartTs, TimeSpan lunchEndTs,
            string modifiedBy)
        {
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

            ConfigurationService.Upsert(db, "Biometrics:Engine:AnalyzeTimeoutMs",
                vm.EngineAnalyzeTimeoutMs.ToString(CultureInfo.InvariantCulture), "int",
                "Timeout in milliseconds for in-process biometric engine analysis.", modifiedBy);

            ConfigurationService.Upsert(db, "Kiosk:MaxConcurrentScans",
                vm.MaxConcurrentScans.ToString(CultureInfo.InvariantCulture), "int",
                "Maximum simultaneous scan requests before 503 is returned.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:CaptureTarget",
                vm.EnrollCaptureTarget.ToString(CultureInfo.InvariantCulture), "int",
                "Target number of frames to collect during enrollment.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:MaxStoredVectors",
                vm.EnrollMaxStoredVectors.ToString(CultureInfo.InvariantCulture), "int",
                "Maximum face vectors stored per enrolled employee.", modifiedBy);

            ConfigurationService.Upsert(db, "Visitors:RecognitionTolerance",
                vm.VisitorRecognitionTolerance.ToString(CultureInfo.InvariantCulture), "double",
                "Face distance tolerance for visitor recognition at kiosk.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:SharpnessThreshold",
                vm.EnrollSharpnessThreshold.ToString(CultureInfo.InvariantCulture), "double",
                "Minimum Laplacian variance score for enrollment frames on desktop.", modifiedBy);

            ConfigurationService.Upsert(db, "Biometrics:Enroll:SharpnessThreshold:Mobile",
                vm.EnrollSharpnessThresholdMobile.ToString(CultureInfo.InvariantCulture), "double",
                "Minimum Laplacian variance score for enrollment frames on mobile.", modifiedBy);

        }

        private static void SaveReviewQueueSettings(FaceAttendDBEntities db, SettingsVm vm, string modifiedBy)
        {
            ConfigurationService.Upsert(
                db,
                "NeedsReview:NearMatchRatio",
                vm.NeedsReviewNearMatchRatio.ToString(CultureInfo.InvariantCulture),
                "double",
                "If distance is within this ratio of the threshold, mark record as NeedsReview.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "NeedsReview:AntiSpoofMargin",
                vm.NeedsReviewAntiSpoofMargin.ToString(CultureInfo.InvariantCulture),
                "double",
                "If anti-spoof is within this margin above threshold, mark record as NeedsReview.",
                modifiedBy);

            ConfigurationService.Upsert(
                db,
                "NeedsReview:GPSAccuracyMargin",
                vm.NeedsReviewGpsMargin.ToString(CultureInfo.InvariantCulture),
                "int",
                "If GPS accuracy is within this margin of the required limit, mark record as NeedsReview.",
                modifiedBy);

        }

        private static void CleanupLegacyKeys(FaceAttendDBEntities db)
        {
            DeleteLegacyModelHashes(db);
            ConfigurationService.Delete(db, "Biometrics:AntiSpoofThreshold");
            ConfigurationService.Delete(db, "Biometrics:MobileAntiSpoofThreshold");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoofInputSize");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:Decision");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:MultiCropScales");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:CropScale");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:RealIndex");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:OutputType");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:Normalize");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:ChannelOrder");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:RunTimeoutMs");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:SlowMs");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:CircuitFailStreak");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:CircuitDisableSeconds");
            ConfigurationService.Delete(db, "Biometrics:AntiSpoof:GateWaitMs");
            ConfigurationService.DeleteByPrefix(db, "Biometrics:Worker:");
            ConfigurationService.Delete(db, "Biometrics:OpenVinoModelsDir");
            ConfigurationService.Delete(db, "DlibTolerance");
            ConfigurationService.Delete(db, "LivenessThreshold");
            ConfigurationService.Delete(db, "Biometrics:DlibPoolSize");
            ConfigurationService.Delete(db, "Biometrics:DlibTolerance");
            ConfigurationService.Delete(db, "Biometrics:SkipLiveness");
            ConfigurationService.Delete(db, "Visitors:DlibTolerance");
            ConfigurationService.Delete(db, "Biometrics:FaceMatchTuner:Enabled");
            ConfigurationService.Delete(db, "Biometrics:FaceMatchTunerEnabled");
            ConfigurationService.DeleteByPrefix(db, "Queue:");
        }

        private static void DeleteLegacyModelHashes(FaceAttendDBEntities db)
        {
            var hashes = ConfigurationService.GetString(db, "Biometrics:ModelHashes", "");
            if (string.IsNullOrWhiteSpace(hashes))
                return;

            if (hashes.IndexOf(".xml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hashes.IndexOf(".bin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hashes.IndexOf("retail-", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ConfigurationService.Delete(db, "Biometrics:ModelHashes");
            }
        }

        private static void SavePerformanceSettings(FaceAttendDBEntities db, SettingsVm vm, string modifiedBy)
        {
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

        }

        private static void SaveVisitorSettings(FaceAttendDBEntities db, SettingsVm vm, string modifiedBy)
        {
            ConfigurationService.Upsert(
                db,
                "Kiosk:VisitorEnabled",
                vm.VisitorEnabled ? "true" : "false",
                "bool",
                "When false, unrecognized faces are rejected instead of opening the visitor form.",
                modifiedBy);

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
                    vm.VisitorEnabled,
                    vm.RecognitionTolerance,
                    vm.AntiSpoofThreshold,
                    vm.GPSAccuracyRequired,
                    vm.GPSRadiusDefault,
                    vm.MinGapSeconds,
                    vm.NeedsReviewNearMatchRatio,
                    vm.NeedsReviewAntiSpoofMargin,
                    vm.NeedsReviewGpsMargin,
                    vm.VisitorMaxRecords,
                    vm.VisitorRetentionYears,
                    savedBy
                });
        }

    }
}
