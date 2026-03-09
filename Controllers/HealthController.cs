using System.Web.Mvc;
using FaceAttend.Services;

namespace FaceAttend.Controllers
{
    [AllowAnonymous]
    [RoutePrefix("health")]
    public class HealthController : Controller
    {
        /// <summary>
        /// Readiness endpoint.
        /// Use this for reverse proxy / origin monitor.
        /// 200 = ready
        /// 503 = may sira / not ready
        /// </summary>
        [HttpGet]
        [Route("")]
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public ActionResult Index()
        {
            var snap = HealthProbe.Check();
            Response.StatusCode = snap.Ready ? 200 : 503;

            return Json(new
            {
                ok = snap.Ready,
                app = snap.App,
                database = snap.Database,
                dlibModelsPresent = snap.DlibModelsPresent,
                livenessModelPresent = snap.LivenessModelPresent,
                livenessCircuitOpen = snap.LivenessCircuitOpen,
                livenessCircuitStuck = snap.LivenessCircuitStuck,
                warmUpState = snap.WarmUpState,
                warmUpMessage = snap.WarmUpMessage,
                timestampUtc = snap.TimestampUtc,
                error = snap.Database ? null : snap.DatabaseError
            }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Liveness endpoint.
        /// Pang "buhay pa ba ang worker process" check lang.
        /// Hindi ito dapat bumagsak kahit may DB issue.
        /// </summary>
        [HttpGet]
        [Route("live")]
        [OutputCache(NoStore = true, Duration = 0, VaryByParam = "*")]
        public ActionResult Live()
        {
            return Json(new
            {
                ok = true,
                app = true
            }, JsonRequestBehavior.AllowGet);
        }
    }
}
