using System;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Services.Helpers;

namespace FaceAttend.Areas.Admin.Helpers
{
    /// <summary>
    /// Validates settings form input.
    /// Extracted from SettingsController to reduce controller size.
    /// </summary>
    public static class SettingsValidator
    {
        /// <summary>
        /// Validates choice fields (enum-like values).
        /// </summary>
        public static void ValidateChoiceFields(SettingsVm vm, ModelStateDictionary modelState)
        {
            if (vm.LivenessDecision != null)
            {
                var value = vm.LivenessDecision.Trim().ToLowerInvariant();
                if (value != "max" && value != "avg")
                    modelState.AddModelError("LivenessDecision", "Use max or avg.");
            }

            if (vm.LivenessOutputType != null)
            {
                var value = vm.LivenessOutputType.Trim().ToLowerInvariant();
                if (value != "logits" && value != "probs")
                    modelState.AddModelError("LivenessOutputType", "Use logits or probs.");
            }

            if (vm.LivenessNormalize != null)
            {
                var value = vm.LivenessNormalize.Trim().ToLowerInvariant();
                if (value != "0_1" && value != "minus1_1" && value != "imagenet" && value != "none")
                    modelState.AddModelError("LivenessNormalize", "Use 0_1, minus1_1, imagenet, or none.");
            }

            if (vm.LivenessChannelOrder != null)
            {
                var value = vm.LivenessChannelOrder.Trim().ToUpperInvariant();
                if (value != "RGB" && value != "BGR")
                    modelState.AddModelError("LivenessChannelOrder", "Use RGB or BGR.");
            }
        }

        /// <summary>
        /// Validates attendance-related fields (time values).
        /// </summary>
        public static void ValidateAttendanceFields(
            SettingsVm vm,
            ModelStateDictionary modelState,
            out TimeSpan workStartTs,
            out TimeSpan workEndTs,
            out TimeSpan lunchStartTs,
            out TimeSpan lunchEndTs)
        {
            workStartTs = TimeSpan.Zero;
            workEndTs = TimeSpan.Zero;
            lunchStartTs = TimeSpan.Zero;
            lunchEndTs = TimeSpan.Zero;

            if (!TimeHelper.TryParseTime(vm.WorkStart, out workStartTs))
                modelState.AddModelError("WorkStart", "Use HH:mm.");

            if (!TimeHelper.TryParseTime(vm.WorkEnd, out workEndTs))
                modelState.AddModelError("WorkEnd", "Use HH:mm.");

            var hasLunchStart = !string.IsNullOrWhiteSpace(vm.LunchStart);
            var hasLunchEnd = !string.IsNullOrWhiteSpace(vm.LunchEnd);

            if (hasLunchStart != hasLunchEnd)
            {
                modelState.AddModelError("LunchStart", "Set both lunch start and lunch end, or leave both blank.");
                modelState.AddModelError("LunchEnd", "Set both lunch start and lunch end, or leave both blank.");
            }
            else if (hasLunchStart && hasLunchEnd)
            {
                if (!TimeHelper.TryParseTime(vm.LunchStart, out lunchStartTs))
                    modelState.AddModelError("LunchStart", "Use HH:mm.");

                if (!TimeHelper.TryParseTime(vm.LunchEnd, out lunchEndTs))
                    modelState.AddModelError("LunchEnd", "Use HH:mm.");
            }

            if (vm.FlexiRequiredHours < 1.0 || vm.FlexiRequiredHours > 12.0)
                modelState.AddModelError("FlexiRequiredHours", "Must be between 1 and 12.");

            if (modelState.IsValid && workEndTs <= workStartTs)
                modelState.AddModelError("WorkEnd", "Work end must be later than work start.");

            if (modelState.IsValid && hasLunchStart && hasLunchEnd && lunchEndTs <= lunchStartTs)
                modelState.AddModelError("LunchEnd", "Lunch end must be later than lunch start.");
        }

        /// <summary>
        /// Validates numeric ranges for settings.
        /// </summary>
        public static void ValidateRanges(SettingsVm vm, ModelStateDictionary modelState)
        {
            if (vm.BallTreeLeafSize < 4 || vm.BallTreeLeafSize > 64)
                modelState.AddModelError("BallTreeLeafSize", "Must be between 4 and 64.");

            if (vm.MaxImageDimension < 320)
                modelState.AddModelError("MaxImageDimension", "Minimum is 320 pixels.");

            if (vm.HalfDayHours < 0.5 || vm.HalfDayHours > 12.0)
                modelState.AddModelError("HalfDayHours", "Must be between 0.5 and 12.");
        }

        /// <summary>
        /// Validates that the fallback office exists if specified.
        /// </summary>
        public static void ValidateFallbackOffice(SettingsVm vm, FaceAttendDBEntities db, ModelStateDictionary modelState)
        {
            if (vm.FallbackOfficeId > 0)
            {
                var exists = db.Offices.Any(o => o.Id == vm.FallbackOfficeId && o.IsActive);
                if (!exists)
                    modelState.AddModelError("FallbackOfficeId", "Select an active office.");
            }
        }

    }
}
