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
    // All biometrics endpoints require admin authentication because they read/write
    // face encodings.
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

                var dlib  = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(path);
                var count = faces == null ? 0 : faces.Length;

                if (count != 1)
                    return Json(new
                    {
                        ok = true,
                        count,
                        liveness   = (float?)null,
                        livenessOk = false
                    });

                var live   = new OnnxLiveness();
                var scored = live.ScoreFromFile(path, faces[0]);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error, count = 1 });

                var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p  = scored.Probability ?? 0f;

                return Json(new
                {
                    ok = true,
                    count = 1,
                    liveness   = p,
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

                var dlib  = new DlibBiometrics();
                var faces = dlib.DetectFacesFromFile(path);

                if (faces == null || faces.Length == 0)
                    return Json(new { ok = false, error = "NO_FACE" });
                if (faces.Length > 1)
                    return Json(new { ok = false, error = "MULTI_FACE" });

                // Server-side liveness enforcement.
                var live   = new OnnxLiveness();
                var scored = live.ScoreFromFile(path, faces[0]);
                if (!scored.Ok)
                    return Json(new { ok = false, error = scored.Error });

                var th = (float)AppSettings.GetDouble("Biometrics:LivenessThreshold", 0.75);
                var p  = scored.Probability ?? 0f;
                if (p < th)
                    return Json(new { ok = false, error = "LIVENESS_FAIL", liveness = p });

                string encErr;
                var vec = dlib.GetSingleFaceEncodingFromFile(path, out encErr);
                if (vec == null)
                    return Json(new { ok = false, error = "ENCODING_FAIL: " + encErr });

                var tol = AppSettings.GetDouble("Biometrics:DlibTolerance", 0.60);

                using (var db = new FaceAttendDBEntities())
                {
                    var emp = db.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
                    if (emp == null)
                        return Json(new { ok = false, error = "EMPLOYEE_NOT_FOUND" });

                    // Duplicate-face check:
                    // Iterate every enrolled employee EXCEPT the one being re-enrolled.
                    // This allows the employee to update their own face (re-enrollment)
                    // while preventing them from registering a face that already belongs
                    // to a different employee (impersonation).
                    //
                    // The `continue` on the matching employeeId is intentional and correct:
                    // it skips the employee's OWN existing encoding so re-enrollment is
                    // always allowed, but every OTHER employee's encoding is still checked.
                    var entries = EmployeeFaceIndex.GetEntries(db);
                    foreach (var e in entries)
                    {
                        if (string.Equals(e.EmployeeId, employeeId,
                                StringComparison.OrdinalIgnoreCase))
                            continue;   // skip self — allows re-enrollment

                        var dist = DlibBiometrics.Distance(vec, e.Vec);
                        if (dist <= tol)
                        {
                            return Json(new
                            {
                                ok = false,
                                error = "FACE_ALREADY_ENROLLED",
                                matchEmployeeId = e.EmployeeId,
                                distance = dist
                            });
                        }
                    }

                    var bytes = DlibBiometrics.EncodeToBytes(vec);
                    emp.FaceEncodingBase64 = Convert.ToBase64String(bytes);
                    emp.LastModifiedDate   = DateTime.UtcNow;
                    emp.ModifiedBy         = "ADMIN";
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

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Saves the uploaded image to a uniquely-named temp file.
        ///
        /// FIX (Path Traversal): the file name is generated entirely from a GUID —
        /// the client-supplied filename is never incorporated into the path.
        /// The resolved path is verified to be within the expected directory as a
        /// defense-in-depth measure.
        /// </summary>
        private static string SaveTemp(HttpPostedFileBase image)
        {
            var tmpRel = "~/App_Data/tmp";
            var tmp    = HostingEnvironment.MapPath(tmpRel);
            if (string.IsNullOrWhiteSpace(tmp))
                throw new InvalidOperationException("TMP_DIR_NOT_FOUND");

            var expectedBase = Path.GetFullPath(tmp);
            Directory.CreateDirectory(expectedBase);

            // Derive extension from MIME type, not from the client filename.
            string ext = ".jpg";
            var ct = (image.ContentType ?? "").ToLowerInvariant().Trim();
            if (ct == "image/png") ext = ".png";

            var fileName     = "u_" + Guid.NewGuid().ToString("N") + ext;
            var fullPath     = Path.Combine(expectedBase, fileName);
            var resolvedPath = Path.GetFullPath(fullPath);

            // Guard: ensure the path is strictly inside the temp directory.
            if (!resolvedPath.StartsWith(
                    expectedBase + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("PATH_TRAVERSAL_DETECTED");
            }

            image.SaveAs(resolvedPath);
            return resolvedPath;
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
