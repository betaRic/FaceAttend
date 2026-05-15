using System;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using FaceAttend.Services.Security;

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
                    vm.FaceCacheStats = FastFaceMatcher.GetStats()?.ToString();
                    
                    // Add TOTP status to view model
                    vm.TotpEnabled = AdminSessionService.IsTotpEnabled();
                    vm.TotpConfigured = TotpService.IsConfigured(AdminSessionService.GetTotpSecret());
                    
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

        // GET: /Admin/Settings/SetupTotp - Show TOTP setup page
        [HttpGet]
        public ActionResult SetupTotp()
        {
            var secret = TotpService.GenerateSecret();
            var label = "FaceAttend Admin";
            var qrUrl = TotpService.GenerateSetupUrl(label, secret);
            
            ViewBag.Secret = secret;
            ViewBag.QrUrl = qrUrl;
            ViewBag.TotpEnabled = AdminSessionService.IsTotpEnabled();
            
            return View();
        }

        // POST: /Admin/Settings/EnableTotp - Enable TOTP with verification code
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult EnableTotp(string secret, string code)
        {
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
            {
                TempData["msg"] = "Secret and verification code are required.";
                return RedirectToAction("SetupTotp");
            }

            // Verify the code works
            if (!TotpService.ValidateCode(secret, code))
            {
                TempData["msg"] = "Invalid verification code. Please ensure your device is synced and try again.";
                return RedirectToAction("SetupTotp");
            }

            // Generate and store recovery codes
            var recoveryCodes = TotpService.GenerateRecoveryCodes();
            var codesHash = string.Join("|", recoveryCodes.Select(TotpService.HashRecoveryCode));
            
            // Store the codes for one-time display (encrypted)
            var codesDisplay = string.Join("\n", recoveryCodes);
            var encryptedCodes = BiometricCrypto.ProtectString(codesDisplay);

            // Save to configuration
            ConfigurationService.Set("Admin:TotpSecret", secret, "string");
            ConfigurationService.Set("Admin:TotpEnabled", "true", "bool");
            ConfigurationService.Set("Admin:TotpRecoveryCodes", codesHash, "string");
            ConfigurationService.Set("Admin:TotpRecoveryCodesStored", encryptedCodes, "string");
            
            // Mark current session as TOTP validated
            AdminAuthorizeAttribute.MarkTotpValidated(Session);

            TempData["msg"] = "TOTP 2FA enabled successfully! Save your recovery codes: " + codesDisplay.Replace("\n", ", ");
            return RedirectToAction("Index");
        }

        // POST: /Admin/Settings/DisableTotp - Disable TOTP (requires PIN)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DisableTotp(string pin)
        {
            var clientIp = Request?.UserHostAddress ?? "";
            if (!AdminAuthorizeAttribute.VerifyPin(pin, clientIp))
            {
                TempData["msg"] = "Invalid PIN. TOTP was NOT disabled.";
                return RedirectToAction("Index");
            }

            ConfigurationService.Set("Admin:TotpEnabled", "false", "bool");
            // Note: We keep the secret so admin can re-enable without new setup
            
            AdminAuthorizeAttribute.ClearTotpValidation(Session);
            
            TempData["msg"] = "TOTP 2FA has been disabled.";
            return RedirectToAction("Index");
        }

        // POST: /Admin/Settings/ChangePin
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ChangePin(string currentPin, string newPin, string confirmPin)
        {
            var clientIp = Request != null ? (Request.UserHostAddress ?? "").Trim() : "";

            if (!AdminPinService.VerifyPin(currentPin, clientIp))
            {
                TempData["msg"] = "Current PIN is incorrect. Admin PIN was NOT changed.";
                return RedirectToAction("Index");
            }

            newPin = (newPin ?? "").Trim();
            confirmPin = (confirmPin ?? "").Trim();

            if (newPin.Length < AdminPinService.MinimumPinLength)
            {
                TempData["msg"] = "New PIN must be at least " + AdminPinService.MinimumPinLength + " characters.";
                return RedirectToAction("Index");
            }

            if (!string.Equals(newPin, confirmPin, StringComparison.Ordinal))
            {
                TempData["msg"] = "New PIN confirmation does not match.";
                return RedirectToAction("Index");
            }

            using (var db = new FaceAttendDBEntities())
            {
                var by = string.IsNullOrWhiteSpace(clientIp) ? "ADMIN" : "ADMIN@" + clientIp;
                ConfigurationService.Upsert(
                    db,
                    "Admin:PinHash",
                    AdminPinService.HashPin(newPin),
                    "string",
                    "PBKDF2 hash for admin PIN. Managed from Admin Settings.",
                    by);

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionSettingChange,
                    "System",
                    "AdminPin",
                    "Admin PIN hash updated from settings.",
                    null,
                    new { storedInDatabase = true });
            }

            ConfigurationService.Invalidate("Admin:PinHash");
            TempData["msg"] = "Admin PIN changed.";
            return RedirectToAction("Index");
        }

        // GET: /Admin/Settings/EnterTotp - Enter TOTP code (when required)
        [HttpGet]
        public ActionResult EnterTotp(string returnUrl)
        {
            if (!AdminSessionService.IsTotpEnabled())
                return RedirectToAction("Index");
                
            if (AdminSessionService.IsTotpValidated(Session))
                return Redirect(string.IsNullOrEmpty(returnUrl) ? "/Admin" : returnUrl);
                
            ViewBag.ReturnUrl = returnUrl ?? "/Admin";
            ViewBag.SecondsRemaining = TotpService.GetSecondsRemaining();
            
            return View();
        }

        // POST: /Admin/Settings/ValidateTotp - Validate TOTP code
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ValidateTotp(string code, string returnUrl)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                ViewBag.Error = "Please enter a code.";
                ViewBag.ReturnUrl = returnUrl ?? "/Admin";
                ViewBag.SecondsRemaining = TotpService.GetSecondsRemaining();
                return View("EnterTotp");
            }

            // Try TOTP code first
            if (AdminAuthorizeAttribute.ValidateTotpCode(code))
            {
                AdminAuthorizeAttribute.MarkTotpValidated(Session);
                return Redirect(string.IsNullOrEmpty(returnUrl) ? "/Admin" : returnUrl);
            }

            // Try recovery code
            if (AdminAuthorizeAttribute.ValidateRecoveryCode(code))
            {
                // Recovery code used - invalidate it
                var currentHash = AdminSessionService.GetRecoveryCodesHash();
                var codes = currentHash.Split('|').ToList();
                var codeIndex = codes.IndexOf(TotpService.HashRecoveryCode(code.ToUpperInvariant()));
                if (codeIndex >= 0)
                {
                    codes[codeIndex] = "USED"; // Mark as used
                    AdminSessionService.SetRecoveryCodesHash(string.Join("|", codes));
                }
                
                AdminAuthorizeAttribute.MarkTotpValidated(Session);
                TempData["msg"] = "Recovery code used successfully. That code is now invalid.";
                return Redirect(string.IsNullOrEmpty(returnUrl) ? "/Admin" : returnUrl);
            }

            ViewBag.Error = "Invalid code. Please try again.";
            ViewBag.ReturnUrl = returnUrl ?? "/Admin";
            ViewBag.SecondsRemaining = TotpService.GetSecondsRemaining();
            return View("EnterTotp");
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
                        FastFaceMatcher.ReloadFromDatabase();

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

        // POST: /Admin/Settings/Save
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Save(SettingsVm vm)
        {
            ViewBag.Title = "Settings";

            if (vm == null)
                return RedirectToAction("Index");

            // Validate
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
