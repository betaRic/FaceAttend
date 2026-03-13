using System.Web.Optimization;

namespace FaceAttend
{
    /// <summary>
    /// A ScriptBundle that skips minification entirely.
    /// 
    /// USE CASE:
    ///   Use this for vendor libraries that are already minified and/or contain
    ///   ES6+ syntax that the WebGrease minifier cannot handle.
    /// 
    /// EXAMPLES:
    ///   - Bootstrap 5+ (uses const, let, arrow functions)
    ///   - SweetAlert2 (uses ES6+ features)
    ///   - Any library that causes JSParser NullReferenceException
    /// 
    /// HOW IT WORKS:
    ///   The default ScriptBundle applies minification transforms when
    ///   BundleTable.EnableOptimizations = true. This class overrides
    ///   the transform behavior to skip minification while still allowing
    ///   bundling (concatenation) of files.
    /// 
    /// ALTERNATIVE:
    ///   For production, consider using CDN links in your layout views
    ///   instead of local ES6+ files. CDNs serve pre-minified files.
    /// </summary>
    public class NonMinifiedScriptBundle : ScriptBundle
    {
        /// <summary>
        /// Creates a new non-minified script bundle.
        /// </summary>
        /// <param name="virtualPath">The virtual path for the bundle (e.g., "~/bundles/bootstrap")</param>
        public NonMinifiedScriptBundle(string virtualPath)
            : base(virtualPath)
        {
            // Remove the default JsMinify transform
            // This prevents the WebGrease JSParser from running
            Transforms.Clear();
            
            // Add a pass-through transform that doesn't minify
            // This still allows bundling (concatenation) without minification
            Transforms.Add(new NonMinifyTransform());
        }

        /// <summary>
        /// Creates a new non-minified script bundle with CDN fallback.
        /// </summary>
        /// <param name="virtualPath">The virtual path for the bundle</param>
        /// <param name="cdnPath">CDN URL for fallback</param>
        public NonMinifiedScriptBundle(string virtualPath, string cdnPath)
            : base(virtualPath, cdnPath)
        {
            Transforms.Clear();
            Transforms.Add(new NonMinifyTransform());
        }
    }

    /// <summary>
    /// A pass-through transform that performs no minification.
    /// Files are concatenated but not minified.
    /// </summary>
    public class NonMinifyTransform : IBundleTransform
    {
        public void Process(BundleContext context, BundleResponse response)
        {
            // Don't do any minification - just pass through the content
            // The files are still concatenated into a single response
            response.ContentType = "text/javascript";
        }
    }

    /// <summary>
    /// A StyleBundle that skips minification entirely.
    /// 
    /// USE CASE:
    ///   Use this for CSS files that contain modern features the WebGrease
    ///   CSS minifier cannot handle, such as CSS custom properties (variables).
    /// 
    /// EXAMPLES:
    ///   - CSS with :root { --my-var: value }
    ///   - CSS with var(--my-var)
    ///   - Modern CSS that causes "Token not allowed after unary operator" errors
    /// 
    /// HOW IT WORKS:
    ///   The default StyleBundle applies CssMinify transform when
    ///   BundleTable.EnableOptimizations = true. This class overrides
    ///   the transform behavior to skip minification while still allowing
    ///   bundling (concatenation) of files.
    /// </summary>
    public class NonMinifiedStyleBundle : StyleBundle
    {
        /// <summary>
        /// Creates a new non-minified style bundle.
        /// </summary>
        /// <param name="virtualPath">The virtual path for the bundle (e.g., "~/Content/admin-enroll")</param>
        public NonMinifiedStyleBundle(string virtualPath)
            : base(virtualPath)
        {
            // Remove the default CssMinify transform
            // This prevents the WebGrease CSS minifier from running
            Transforms.Clear();
            
            // Add a pass-through transform that doesn't minify
            // This still allows bundling (concatenation) without minification
            Transforms.Add(new CssNonMinifyTransform());
        }

        /// <summary>
        /// Creates a new non-minified style bundle with CDN fallback.
        /// </summary>
        /// <param name="virtualPath">The virtual path for the bundle</param>
        /// <param name="cdnPath">CDN URL for fallback</param>
        public NonMinifiedStyleBundle(string virtualPath, string cdnPath)
            : base(virtualPath, cdnPath)
        {
            Transforms.Clear();
            Transforms.Add(new CssNonMinifyTransform());
        }
    }

    /// <summary>
    /// A pass-through transform that performs no CSS minification.
    /// Files are concatenated but not minified.
    /// </summary>
    public class CssNonMinifyTransform : IBundleTransform
    {
        public void Process(BundleContext context, BundleResponse response)
        {
            // Don't do any minification - just pass through the content
            // The files are still concatenated into a single response
            response.ContentType = "text/css";
        }
    }
}
