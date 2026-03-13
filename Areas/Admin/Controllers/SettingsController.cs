using System;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class SettingsController : Controller
    {
        // GET: /Admin/Settings
        [HttpGet]
        public ActionResult Index()
        {
            ViewBag.Title = "Settings";

            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    var vm = SettingsViewModelBuilder.BuildVm(db);
                    vm.SavedMessage = TempData["msg"] as string;
                    ViewBag.FaceCacheStats = FastFaceMatcher.GetStats()?.ToString();
                    
                    // Liveness diagnostics
                    ViewBag.LivenessThreshold = ConfigurationService.GetDouble(
                        db, "Biometrics:LivenessThreshold",
                        ConfigurationService.GetDouble("Biometrics:LivenessThreshold", 0.75));
                    var modelPath = System.Web.Hosting.HostingEnvironment.MapPath(
                        ConfigurationService.GetString("Biometrics:LivenessModelPath", "~/App_Data/models/liveness/minifasnet.onnx"));
                    ViewBag.LivenessModelPath = modelPath;
                    ViewBag.LivenessModelExists = System.IO.File.Exists(modelPath);
                    
                    return View(vm);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("[Settings] BuildVm failed: " + ex);

                var safeVm = SettingsViewModelBuilder.BuildSafeVm(ex.Message);
                safeVm.SavedMessage = TempData["msg"] as string;
                return View(safeVm);
            }
        }

        // POST: /Admin/Settings/Heartbeat
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Heartbeat()
        {
            var refreshed = AdminAuthorizeAttribute.RefreshSession(Session);
            return Json(new
            {
                ok = refreshed,
                expiresIn = refreshed
                    ? AdminAuthorizeAttribute.GetRemainingSessionSeconds(Session)
                    : 0
            });
        }

        // POST: /Admin/Settings/MigrateBiometricEncryption
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult MigrateBiometricEncryption()
        {
            try
            {
                using (var db = new FaceAttendDBEntities())
                {
                    int employeePrimary = 0;
                    int employeeJson = 0;
                    int visitorPrimary = 0;

                    var employees = db.Employees
                        .Where(e => e.FaceEncodingBase64 != null || e.FaceEncodingsJson != null)
                        .ToList();

                    foreach (var emp in employees)
                    {
                        if (BiometricCrypto.NeedsMigration(emp.FaceEncodingBase64))
                        {
                            emp.FaceEncodingBase64 = BiometricCrypto.ProtectString(emp.FaceEncodingBase64);
                            employeePrimary++;
                        }

                        if (BiometricCrypto.NeedsMigration(emp.FaceEncodingsJson))
                        {
                            emp.FaceEncodingsJson = BiometricCrypto.ProtectString(emp.FaceEncodingsJson);
                            employeeJson++;
                        }
                    }

                    var visitors = db.Visitors
                        .Where(v => v.FaceEncodingBase64 != null)
                        .ToList();

                    foreach (var v in visitors)
                    {
                        if (BiometricCrypto.NeedsMigration(v.FaceEncodingBase64))
                        {
                            v.FaceEncodingBase64 = BiometricCrypto.ProtectString(v.FaceEncodingBase64);
                            visitorPrimary++;
                        }
                    }

                    db.SaveChanges();

                    if (employeePrimary > 0 || employeeJson > 0)
                        EmployeeFaceIndex.Invalidate();

                    if (visitorPrimary > 0)
                        VisitorFaceIndex.Invalidate();

                    AuditHelper.Log(
                        db,
                        Request,
                        AuditHelper.ActionSettingChange,
                        "System",
                        "BiometricEncryptionMigration",
                        "Nag-run ng one-time biometric at-rest encryption migration.",
                        null,
                        new
                        {
                            employeePrimary,
                            employeeJson,
                            visitorPrimary
                        });

                    TempData["msg"] =
                        "Biometric encryption migration done. " +
                        "Employees(primary): " + employeePrimary + ", " +
                        "Employees(json): " + employeeJson + ", " +
                        "Visitors: " + visitorPrimary + ".";
                }
            }
            catch (Exception ex)
            {
                TempData["msg"] = "Biometric migration failed: " + ex.Message;
            }

            return RedirectToAction("Index");
        }

        // POST: /Admin/Settings/ReloadFaceCache
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ReloadFaceCache()
        {
            try
            {
                FastFaceMatcher.ReloadFromDatabase();
                TempData["msg"] = "Face cache reloaded. " + FastFaceMatcher.GetStats();
            }
            catch (Exception ex)
            {
                TempData["msg"] = "Failed to reload cache: " + ex.Message;
            }
            return RedirectToAction("Index");
        }

        // GET: /Admin/Settings/ResetLivenessCircuit
        [HttpGet]
        public ActionResult ResetLivenessCircuit()
        {
            try
            {
                OnnxLiveness.ResetCircuit();
                TempData["msg"] = "Liveness circuit breaker reset successfully.";
            }
            catch (Exception ex)
            {
                TempData["msg"] = "Failed to reset circuit: " + ex.Message;
            }
            return RedirectToAction("Index");
        }

        // POST: /Admin/Settings/Save
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Save(SettingsVm vm)
        {
            ViewBag.Title = "Settings";

            if (vm == null)
                return RedirectToAction("Index");

            // Validate
            SettingsValidator.ValidateChoiceFields(vm, ModelState);
            SettingsValidator.ValidateRanges(vm, ModelState);

            TimeSpan workStartTs;
            TimeSpan workEndTs;
            TimeSpan lunchStartTs;
            TimeSpan lunchEndTs;

            SettingsValidator.ValidateAttendanceFields(
                vm,
                ModelState,
                out workStartTs,
                out workEndTs,
                out lunchStartTs,
                out lunchEndTs);

            using (var db = new FaceAttendDBEntities())
            {
                SettingsValidator.ValidateFallbackOffice(vm, db, ModelState);

                if (!ModelState.IsValid)
                {
                    vm.OfficeOptions = AdminQueryHelper.BuildOfficeOptionsWithAuto(db, vm.FallbackOfficeId);
                    return View("Index", vm);
                }

                var clientIp = Request != null ? (Request.UserHostAddress ?? "").Trim() : "";
                var by = string.IsNullOrWhiteSpace(clientIp) ? "ADMIN" : "ADMIN@" + clientIp;

                // Save all settings
                SettingsSaver.SaveSettings(
                    db, vm,
                    workStartTs, workEndTs,
                    lunchStartTs, lunchEndTs,
                    by);

                // Invalidate caches
                ConfigurationService.InvalidateAll();

                // Audit log
                SettingsSaver.LogSettingsChange(db, Request, vm, by);

                TempData["msg"] = "Settings saved.";
                return RedirectToAction("Index");
            }
        }
    }
}
