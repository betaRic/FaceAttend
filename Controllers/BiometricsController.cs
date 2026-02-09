using System;
using System.Configuration;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Storage;
using FaceAttend.Models;

namespace FaceAttend.Controllers
{
    public class BiometricsController : Controller
    {
        private static readonly Lazy<DlibBiometrics> _dlib = new Lazy<DlibBiometrics>(() => new DlibBiometrics());
        private static readonly Lazy<OnnxLiveness> _liv = new Lazy<OnnxLiveness>(() => new OnnxLiveness());

        private readonly IEmployeeRepository _repo = new JsonEmployeeRepository();

        [HttpGet]
        public ActionResult Health()
        {
            var tol = double.Parse(ConfigurationManager.AppSettings["Biometrics:DlibTolerance"] ?? "0.60");
            var det = (ConfigurationManager.AppSettings["Biometrics:DlibDetector"] ?? "hog");
            return Json(new { ok = true, dlib = true, onnx = true, detector = det, tolerance = tol }, JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        public ActionResult Detect(HttpPostedFileBase image)
        {
            if (image == null) return Json(new { ok = false, error = "NO_IMAGE" });

            var faces = _dlib.Value.DetectFaces(image);
            return Json(new { ok = true, count = faces.Length, faces });
        }

        // NEW: single call = detect + liveness
        [HttpPost]
        public ActionResult ScanFrame(HttpPostedFileBase image)
        {
            if (image == null) return Json(new { ok = false, error = "NO_IMAGE" });

            var faces = _dlib.Value.DetectFaces(image);
            if (faces == null || faces.Length == 0)
                return Json(new { ok = true, count = 0, faces = new object[0], liveness = (float?)null });

            if (faces.Length > 1)
                return Json(new { ok = true, count = faces.Length, faces, liveness = (float?)null });

            // exactly 1 face -> run liveness
            var fb = faces[0];
            image.InputStream.Position = 0; // important: reuse stream
            var score = _liv.Value.ScoreFromImage(image, fb);

            return Json(new
            {
                ok = true,
                count = 1,
                faces,
                liveness = score.Probability,
                livenessOk = score.Ok && score.Probability.HasValue
            });
        }

        [HttpPost]
        public ActionResult LivenessStill(HttpPostedFileBase image)
        {
            if (image == null) return Json(new { ok = false, error = "NO_IMAGE" });

            var faces = _dlib.Value.DetectFaces(image);
            if (faces == null || faces.Length != 1)
                return Json(new { ok = false, error = (faces == null || faces.Length == 0) ? "NO_FACE" : "MULTIPLE_FACES" });

            image.InputStream.Position = 0;
            var score = _liv.Value.ScoreFromImage(image, faces[0]);

            return Json(new { ok = score.Ok, probability = score.Probability, error = score.Error });
        }

        [HttpPost]
        public ActionResult Enroll(string employeeId, HttpPostedFileBase image)
        {
            if (string.IsNullOrWhiteSpace(employeeId)) return Json(new { ok = false, error = "NO_EMPLOYEE_ID" });
            if (image == null) return Json(new { ok = false, error = "NO_IMAGE" });

            employeeId = employeeId.Trim().ToUpperInvariant();

            var enc = _dlib.Value.GetSingleFaceEncoding(image, out var err);
            if (enc == null) return Json(new { ok = false, error = err ?? "NO_FACE_OR_LOW_QUALITY" });

            var tol = double.Parse(ConfigurationManager.AppSettings["Biometrics:DlibTolerance"] ?? "0.60");

            foreach (var r in _repo.GetAll())
            {
                if (string.IsNullOrWhiteSpace(r.FaceTemplateBase64)) continue;
                var existing = Convert.FromBase64String(r.FaceTemplateBase64);
                var v = DlibBiometrics.DecodeFromBytes(existing);
                var dist = DlibBiometrics.Distance(enc, v);
                if (dist <= tol)
                {
                    return Json(new { ok = false, error = "DUPLICATE_FACE", matchedEmployeeId = r.EmployeeId, distance = dist });
                }
            }

            _repo.Upsert(new EmployeeFaceRecord
            {
                EmployeeId = employeeId,
                FaceTemplateBase64 = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(enc)),
                CreatedUtc = DateTime.UtcNow.ToString("o")
            });

            return Json(new { ok = true, employeeId });
        }

        [HttpPost]
        public ActionResult Identify(HttpPostedFileBase image)
        {
            if (image == null) return Json(new { ok = false, error = "NO_IMAGE" });

            var enc = _dlib.Value.GetSingleFaceEncoding(image, out var err);
            if (enc == null) return Json(new { ok = false, error = err ?? "NO_FACE_OR_LOW_QUALITY" });

            var tol = double.Parse(ConfigurationManager.AppSettings["Biometrics:DlibTolerance"] ?? "0.60");

            var best = _repo.GetAll()
                .Select(r => new
                {
                    r.EmployeeId,
                    Vec = string.IsNullOrWhiteSpace(r.FaceTemplateBase64) ? null : DlibBiometrics.DecodeFromBytes(Convert.FromBase64String(r.FaceTemplateBase64))
                })
                .Where(x => x.Vec != null)
                .Select(x => new
                {
                    x.EmployeeId,
                    Dist = DlibBiometrics.Distance(enc, x.Vec)
                })
                .OrderBy(x => x.Dist)
                .FirstOrDefault();

            if (best == null || best.Dist > tol)
                return Json(new { ok = false, error = "NO_MATCH", best });

            // Convert distance -> similarity (0..1) for display only
            var sim = Math.Max(0.0, 1.0 - (best.Dist / tol));
            return Json(new { ok = true, employeeId = best.EmployeeId, similarity = sim, distance = best.Dist });
        }
    }
}
