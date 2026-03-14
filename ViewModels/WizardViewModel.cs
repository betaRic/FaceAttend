using System.Collections.Generic;

namespace FaceAttend.ViewModels
{
    /// <summary>
    /// View model for the Wizard component
    /// </summary>
    public class WizardViewModel
    {
        /// <summary>
        /// Unique identifier for the wizard instance
        /// </summary>
        public string Id { get; set; } = "wizard";

        /// <summary>
        /// Array of step labels
        /// </summary>
        public string[] Steps { get; set; } = new string[0];

        /// <summary>
        /// Current active step (1-based)
        /// </summary>
        public int CurrentStep { get; set; } = 1;

        /// <summary>
        /// Size variant of the wizard
        /// </summary>
        public WizardSize Size { get; set; } = WizardSize.Medium;

        /// <summary>
        /// Visual variant of the wizard
        /// </summary>
        public WizardVariant Variant { get; set; } = WizardVariant.Default;
    }

    /// <summary>
    /// Size variants for the wizard component
    /// </summary>
    public enum WizardSize
    {
        Small,
        Medium,
        Large
    }

    /// <summary>
    /// Visual variants for the wizard component
    /// </summary>
    public enum WizardVariant
    {
        Default,
        Minimal
    }
}
