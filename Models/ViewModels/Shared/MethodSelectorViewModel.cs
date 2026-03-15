using System.Collections.Generic;

namespace FaceAttend.Models.ViewModels.Shared
{
    public class MethodSelectorViewModel
    {
        public string Id { get; set; }
        public List<MethodOption> Methods { get; set; } = new List<MethodOption>();
        public string OnSelect { get; set; } = "";
    }

    public class MethodOption
    {
        public string Id { get; set; }
        public string Icon { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public bool Recommended { get; set; } = false;
        public string Badge { get; set; } = "";
    }
}
