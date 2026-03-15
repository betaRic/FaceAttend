namespace FaceAttend.Models.ViewModels.Admin
{
    public class WizardViewModel
    {
        public string Id { get; set; }
        public string[] Steps { get; set; }
        public int CurrentStep { get; set; }
        public WizardSize Size { get; set; } = WizardSize.Medium;
        public WizardVariant Variant { get; set; } = WizardVariant.Default;
    }

    public enum WizardSize
    {
        Small,
        Medium,
        Large
    }

    public enum WizardVariant
    {
        Default,
        Minimal
    }
}
