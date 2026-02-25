using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using System.Threading.Tasks;
using FaceAttend.Services.Biometrics;  // P1-F2: required for DlibBiometrics + OnnxLiveness

namespace FaceAttend
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);

            // Warm up Dlib + ONNX so the first scan is not slow.
            Task.Run(() =>
            {
                try { new DlibBiometrics(); } catch { }
                try { OnnxLiveness.WarmUp(); } catch { }
            });
        }

        // P1-F2: Dispose unmanaged Dlib and ONNX resources on IIS app pool recycle.
        // Both DisposeInstance() and DisposeSession() are fully implemented in their
        // respective classes but were never called — this was a resource leak on every
        // recycle. Application_End is the correct place: it is called once per app
        // domain shutdown, before the process terminates.
        protected void Application_End()
        {
            DlibBiometrics.DisposeInstance();
            OnnxLiveness.DisposeSession();
        }
    }
}
