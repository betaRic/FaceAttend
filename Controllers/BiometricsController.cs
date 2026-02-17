using System;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Web.Mvc;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Controllers
{
    // Biometrics endpoints used by the Admin enrollment wizard.
    // Keep these behind AdminAuthorize because they write face encodings.
    [AdminAuthorize]
    public class BiometricsController : Controller
    {
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ScanFrame(HttpPostedFileBase image)
        {
            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            try
            {
                path = SaveTemp(image);

                var dlib = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(path);
                var count = faces == null ? 0 : faces.Length;

                if (count != 1)
                    return Json(new { ok = true, count = count, liveness = (float?)null, livenessOk = false });

                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(path, faces[0]);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error, count = 1 });

                var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p = scored.Probability ?? 0f;

                return Json(new
                {
                    ok = true,
                    count = 1,
                    liveness = p,
                    livenessOk = p >= th
                });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "SCAN_ERROR: " + ex.Message });
            }
            finally
            {
                TryDelete(path);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Enroll(string employeeId, HttpPostedFileBase image)
        {
            employeeId = (employeeId ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(employeeId))
                return Json(new { ok = false, error = "NO_EMPLOYEE_ID" });

            if (image == null || image.ContentLength <= 0)
                return Json(new { ok = false, error = "NO_IMAGE" });

            var max = AppSettings.GetInt("Biometrics:MaxUploadBytes", 10 * 1024 * 1024);
            if (image.ContentLength > max)
                return Json(new { ok = false, error = "TOO_LARGE" });

            string path = null;
            try
            {
                path = SaveTemp(image);

                var dlib = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(path);

                if (faces == null || faces.Length == 0)
                    return Json(new { ok = false, error = "NO_FACE" });

                if (faces.Length > 1)
                    return Json(new { ok = false, error = "MULTI_FACE" });

                // Liveness (server enforcement)
                var live = new OnnxLiveness();
                var scored = live.ScoreFromFile(path, faces[0]);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error });

                var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p = scored.Probability ?? 0f;
                if (p < th)
                    return Json(new { ok = false, error = "LIVENESS_FAIL", liveness = p });

                // Encoding
                string encErr;
                var vec = dlib.GetSingleFaceEncodingFromFile(path, out encErr);
                if (vec == null)
                    return Json(new { ok = false, error = "ENCODING_FAIL: " + encErr });

                // Duplicate check
                var tol = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);
                using (var db = new FaceAttendDBEntities())
                {
                    var emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                    if (emp == null)
                        return Json(new { ok = false, error = "EMPLOYEE_NOT_FOUND" });

                    var entries = EmployeeFaceIndex.GetEntries(db);
                    foreach (var e in entries)
                    {
                        if (string.Equals(e.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var dist = DlibBiometrics.Distance(vec, e.Vec);
                        if (dist <= tol)
                        {
                            return Json(new { ok = false, error = "FACE_ALREADY_ENROLLED", matchEmployeeId = e.EmployeeId, distance = dist });
                        }
                    }

                    // Save
                    var bytes = DlibBiometrics.EncodeToBytes(vec);
                    emp.FaceEncodingBase64 = Convert.ToBase64String(bytes);
                    emp.LastModifiedDate = DateTime.UtcNow;
                    emp.ModifiedBy = "ADMIN";
                    db.SaveChanges();

                    EmployeeFaceIndex.Invalidate();
                }

                return Json(new { ok = true, liveness = p });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "ENROLL_ERROR: " + ex.Message });
            }
            finally
            {
                TryDelete(path);
            }
        }

        private static string SaveTemp(HttpPostedFileBase image)
        {
            var tmpRel = "~/App_Data/tmp";
            var tmp = HostingEnvironment.MapPath(tmpRel);
            if (string.IsNullOrWhiteSpace(tmp))
                throw new InvalidOperationException("TMP_DIR_NOT_FOUND");

            Directory.CreateDirectory(tmp);

            var ext = ".jpg";
            var name = (image.FileName ?? "").ToLowerInvariant();
            if (name.EndsWith(".png")) ext = ".png";

            var file = Path.Combine(tmp, "u_" + Guid.NewGuid().ToString("N") + ext);
            image.SaveAs(file);
            return file;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch { }
        }
    }
}
