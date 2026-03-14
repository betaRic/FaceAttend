using System.Collections.Generic;

namespace FaceAttend.ViewModels
{
    /// <summary>
    /// View model for the MethodSelector component
    /// </summary>
    public class MethodSelectorViewModel
    {
        /// <summary>
        /// Unique identifier for the component
        /// </summary>
        public string Id { get; set; } = "methodSelector";

        /// <summary>
        /// Available method options
        /// </summary>
        public List<MethodOption> Methods { get; set; } = new List<MethodOption>();

        /// <summary>
        /// JavaScript callback function name when a method is selected
        /// </summary>
        public string OnSelect { get; set; } = "onMethodSelect";
    }

    /// <summary>
    /// Individual method option
    /// </summary>
    public class MethodOption
    {
        /// <summary>
        /// Unique identifier for the method
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// FontAwesome icon class
        /// </summary>
        public string Icon { get; set; } = "fa-circle";

        /// <summary>
        /// Method title
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Method description
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Whether this method is recommended
        /// </summary>
        public bool Recommended { get; set; } = false;

        /// <summary>
        /// Optional badge text
        /// </summary>
        public string Badge { get; set; } = "";
    }
}
