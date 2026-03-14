namespace FaceAttend.ViewModels
{
    /// <summary>
    /// View model for the ProcessingModal component
    /// </summary>
    public class ProcessingModalViewModel
    {
        /// <summary>
        /// Unique identifier for the modal
        /// </summary>
        public string Id { get; set; } = "processingModal";

        /// <summary>
        /// Modal title
        /// </summary>
        public string Title { get; set; } = "Processing...";

        /// <summary>
        /// Initial status message
        /// </summary>
        public string InitialStatus { get; set; } = "Initializing...";

        /// <summary>
        /// Whether to show the progress bar
        /// </summary>
        public bool ShowProgress { get; set; } = true;

        /// <summary>
        /// Whether the modal is dismissible
        /// </summary>
        public bool Dismissible { get; set; } = false;
    }
}
