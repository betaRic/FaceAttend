using System.Linq;
using System.Web.Mvc;
using System.Data.Entity.Validation;

namespace FaceAttend.Infrastructure
{
    /// <summary>
    /// Extension methods for MVC controllers.
    /// </summary>
    public static class ControllerExtensions
    {
        /// <summary>
        /// Adds all EF entity validation errors from a DbEntityValidationException
        /// to the ModelState as model-level errors ("" key).
        /// </summary>
        public static void AddDbValidationErrors(this ModelStateDictionary modelState, DbEntityValidationException ex)
        {
            var errors = ex.EntityValidationErrors
                .SelectMany(e => e.ValidationErrors)
                .Select(e => $"{e.PropertyName}: {e.ErrorMessage}");

            modelState.AddModelError("", "Validation error: " + string.Join("; ", errors));
        }
    }
}
