using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using FaceAttend.Models;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Storage;

namespace FaceAttend.Controllers
{
    public class BiometricsController : Controller
    {
        private static readonly Lazy<DlibBiometrics> _dlib = new Lazy<DlibBiometrics>(() => new DlibBiometrics());
        private static readonly Lazy<OnnxLiveness> _liv = new Lazy<OnnxLiveness>(() => new OnnxLiveness());

        private readonly IEmployeeRepository _repo = new JsonEmployeeRepository();

        private int MaxUploadBytes
        {
            get
            {
                if (int.TryParse(ConfigurationManager.AppSettings["Biometrics:MaxUploadBytes"], out var b) && b > 0)
                    return b;
                return 10 * 1024 * 1024;
            }
        }

        private int EmployeeIdMaxLen
        {
            get
            {
                if (int.TryParse(ConfigurationManager.AppSettings["Biometrics:EmployeeIdMaxLen"], out var n) && n > 0)
                    return n;
                return 20;
            }
        }

        private string EmployeeIdPattern =>
            (ConfigurationManager.AppSettings["Biometrics:EmployeeIdPattern"] ?? "^[A-Z0-9_-]{1,20}$").Trim();

        private bool TryGetTolerance(out double tol, out string err)
        {
            err = null;
            tol = 0.60;

            var s = (ConfigurationManager.AppSettings["Biometrics:DlibTolerance"] ?? "0.60").Trim();
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out tol))
            {
                err = "BAD_TOLERANCE";
                tol = 0.60;
                return false;
            }

            // Distance tolerance sanity guard
            if (tol < 0.20 || tol > 1.00)
            {
                err = "TOLERANCE_OUT_OF_RANGE";
                return false;
            }

            return true;
        }

        private bool IsValidEmployeeId(string employeeId, out string error)
        {
            error = null;

            var id = (employeeId ?? "").Trim().ToUpperInvariant();
            if (id.Length == 0) { error = "NO_EMPLOYEE_ID"; return false; }
            if (id.Length > EmployeeIdMaxLen) { error = "EMPLOYEE_ID_TOO_LONG"; return false; }

            try
            {
                if (!Regex.IsMatch(id, EmployeeIdPattern))
                {
                    error = "EMPLOYEE_ID_INVALID";
                    return false;
                }
            }
            catch
            {
                error = "EMPLOYEE_ID_PATTERN_BAD";
                return false;
            }

            return true;
        }

        [HttpGet]
        public ActionResult Health()
        {
            bool dlibOk = false, onnxOk = false;
            string dlibErr = null, onnxErr = null;

            try { var _ = _dlib.Value; dlibOk = true; }
            catch (Exception ex) { dlibErr = ex.Message; }

            try { var _ = _liv.Value; onnxOk = true; }
            catch (Exception ex) { onnxErr = ex.Message; }

            var det = (ConfigurationManager.AppSettings["Biometrics:DlibDetector"] ?? "hog").Trim();

            var tolOk = TryGetTolerance(out var tol, out var tolErr);

            // IMPORTANT: Health must never crash
            return Json(new
            {
                ok = dlibOk && onnxOk && tolOk,
                dlib = dlibOk,
                onnx = onnxOk,
                detector = det,
                tolerance = tol,
                toleranceOk = tolOk,
                toleranceError = tolErr,
                maxUploadBytes = MaxUploadBytes,
                employeeIdMaxLen = EmployeeIdMaxLen,
                employeeIdPattern = EmployeeIdPattern,
                cacheCount = EmployeeFaceIndex.GetEntries(_repo).Count,
                dlibErr,
                onnxErr
            }, JsonRequestBehavior.AllowGet);
        }

        private bool ValidateImage(HttpPostedFileBase image, out ActionResult errorResult)
        {
            if (image == null) { errorResult = Json(new { ok = false, error = "NO_IMAGE" }); return false; }
            if (image.ContentLength <= 0) { errorResult = Json(new { ok = false, error = "EMPTY_IMAGE" }); return false; }
            if (image.ContentLength > MaxUploadBytes) { errorResult = Json(new { ok = false, error = "IMAGE_TOO_LARGE", maxBytes = MaxUploadBytes }); return false; }

            errorResult = null;
            return true;
        }

        private string SaveUploadToTemp(HttpPostedFileBase file)
        {
            var tmpDir = Server.MapPath("~/App_Data/_tmp");
            Directory.CreateDirectory(tmpDir);

            var ext = (Path.GetExtension(file.FileName) ?? "").Trim().ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") ext = ".jpg";

            var path = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ext);
            file.SaveAs(path);
            return path;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch { }
        }

        [HttpPost]
        public ActionResult Detect(HttpPostedFileBase image)
        {
            if (!ValidateImage(image, out var bad)) return bad;
            var faces = _dlib.Value.DetectFaces(image);
            return Json(new { ok = true, count = faces.Length, faces });
        }

        [HttpPost]
        public ActionResult ScanFrame(HttpPostedFileBase image, bool debug = false)
        {
            if (!ValidateImage(image, out var bad)) return bad;

            string path = null;
            try
            {
                path = SaveUploadToTemp(image);

                var faces = _dlib.Value.DetectFacesFromFile(path);
                if (faces == null || faces.Length == 0)
                    return Json(new { ok = true, count = 0, faces = new object[0], liveness = (float?)null, livenessOk = false });

                if (faces.Length > 1)
                    return Json(new { ok = true, count = faces.Length, faces, liveness = (float?)null, livenessOk = false });

                var fb = faces[0];
                var score = _liv.Value.ScoreFromFile(path, fb);

                return Json(new
                {
                    ok = true,
                    count = 1,
                    faces,
                    liveness = score.Probability,
                    livenessOk = score.Ok && score.Probability.HasValue,
                    livenessError = debug ? score.Error : null
                });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "SCAN_FAILED", message = debug ? ex.Message : null });
            }
            finally
            {
                SafeDelete(path);
            }
        }

        [HttpPost]
        public ActionResult LivenessStill(HttpPostedFileBase image, bool debug = false)
        {
            if (!ValidateImage(image, out var bad)) return bad;

            string path = null;
            try
            {
                path = SaveUploadToTemp(image);

                var faces = _dlib.Value.DetectFacesFromFile(path);
                if (faces == null || faces.Length != 1)
                    return Json(new { ok = false, error = (faces == null || faces.Length == 0) ? "NO_FACE" : "MULTIPLE_FACES" });

                var score = _liv.Value.ScoreFromFile(path, faces[0]);
                return Json(new { ok = score.Ok, probability = score.Probability, error = debug ? score.Error : null });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "LIVENESS_FAILED", message = debug ? ex.Message : null });
            }
            finally
            {
                SafeDelete(path);
            }
        }

        [HttpPost]
        public ActionResult Enroll(string employeeId, HttpPostedFileBase image)
        {
            if (!ValidateImage(image, out var bad)) return bad;

            employeeId = (employeeId ?? "").Trim().ToUpperInvariant();

            if (!IsValidEmployeeId(employeeId, out var idErr))
                return Json(new { ok = false, error = idErr });

            if (!TryGetTolerance(out var tol, out var tolErr))
                return Json(new { ok = false, error = tolErr ?? "BAD_TOLERANCE" });

            var enc = _dlib.Value.GetSingleFaceEncoding(image, out var err);
            if (enc == null) return Json(new { ok = false, error = err ?? "NO_FACE_OR_LOW_QUALITY" });

            // Duplicate check against cached vectors (fast)
            var entries = EmployeeFaceIndex.GetEntries(_repo);
            foreach (var e in entries)
            {
                if (e == null || e.Vec == null) continue;
                if (string.Equals(e.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)) continue;

                var dist = DlibBiometrics.Distance(enc, e.Vec);
                if (dist <= tol)
                    return Json(new { ok = false, error = "DUPLICATE_FACE", matchedEmployeeId = e.EmployeeId, distance = dist });
            }

            _repo.Upsert(new EmployeeFaceRecord
            {
                EmployeeId = employeeId,
                FaceTemplateBase64 = Convert.ToBase64String(DlibBiometrics.EncodeToBytes(enc)),
                CreatedUtc = DateTime.UtcNow.ToString("o")
            });

            EmployeeFaceIndex.Rebuild(_repo);

            return Json(new { ok = true, employeeId });
        }

        [HttpPost]
        public ActionResult Identify(HttpPostedFileBase image)
        {
            if (!ValidateImage(image, out var bad)) return bad;

            if (!TryGetTolerance(out var tol, out var tolErr))
                return Json(new { ok = false, error = tolErr ?? "BAD_TOLERANCE" });

            var enc = _dlib.Value.GetSingleFaceEncoding(image, out var err);
            if (enc == null) return Json(new { ok = false, error = err ?? "NO_FACE_OR_LOW_QUALITY" });

            var entries = EmployeeFaceIndex.GetEntries(_repo);

            string bestId = null;
            double bestDist = double.PositiveInfinity;

            foreach (var e in entries)
            {
                if (e == null || e.Vec == null) continue;

                var dist = DlibBiometrics.Distance(enc, e.Vec);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestId = e.EmployeeId;
                }
            }

            if (bestId == null || bestDist > tol)
                return Json(new { ok = false, error = "NO_MATCH" });

            var sim = Math.Max(0.0, 1.0 - (bestDist / tol));
            return Json(new { ok = true, employeeId = bestId, similarity = sim, distance = bestDist });
        }
    }
}
