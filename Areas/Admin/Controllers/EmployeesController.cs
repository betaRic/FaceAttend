using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class EmployeesController : Controller
    {
        public ActionResult Index(string q, int? page, string status)
        {
            using (var db = new FaceAttendDBEntities())
            {
                int pageSize = ConfigurationService.GetInt("Employees:PageSize", 200);
                if (pageSize < 25) pageSize = 25;
                if (pageSize > 1000) pageSize = 1000;

                int p = page.GetValueOrDefault(1);
                if (p < 1) p = 1;

                var normalizedStatus = NormalizeStatus(status);
                var allRows = QueryEmployeeRows(db, q, normalizedStatus);
                var total = allRows.Count;
                var totalPages = (int)Math.Ceiling(total / (double)pageSize);
                if (totalPages <= 0) totalPages = 1;
                if (p > totalPages) p = totalPages;

                var skip = Math.Max(0, (p - 1) * pageSize);
                var rows = allRows.Skip(skip).Take(pageSize).ToList();

                ViewBag.Q = q ?? "";
                ViewBag.Page = p;
                ViewBag.PageSize = pageSize;
                ViewBag.Total = total;
                ViewBag.StatusFilter = normalizedStatus;

                return View(rows);
            }
        }

        [HttpGet]
        public ActionResult Create()
        {
            using (var db = new FaceAttendDBEntities())
            {
                SetOffices(db, null);
                return View(new EmployeeEditVm
                {
                    IsActive = true,
                    Status = "ACTIVE"
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create(EmployeeEditVm vm)
        {
            vm.EmployeeId = (vm.EmployeeId ?? "").Trim().ToUpperInvariant();
            vm.FirstName = (vm.FirstName ?? "").Trim();
            vm.MiddleName = (vm.MiddleName ?? "").Trim();
            vm.LastName = (vm.LastName ?? "").Trim();
            vm.Position = (vm.Position ?? "").Trim();
            vm.Status = "ACTIVE";
            vm.IsActive = true;

            using (var db = new FaceAttendDBEntities())
            {
                if (!ModelState.IsValid)
                {
                    SetOffices(db, vm.OfficeId);
                    return View(vm);
                }

                if (db.Employees.Any(e => e.EmployeeId == vm.EmployeeId))
                {
                    ModelState.AddModelError("EmployeeId", "EmployeeId already exists.");
                    SetOffices(db, vm.OfficeId);
                    return View(vm);
                }

                var actor = AuditHelper.GetActorIp(Request);
                var emp = new Employee
                {
                    EmployeeId = vm.EmployeeId,
                    FirstName = vm.FirstName,
                    MiddleName = string.IsNullOrWhiteSpace(vm.MiddleName) ? null : vm.MiddleName,
                    LastName = vm.LastName,
                    Position = string.IsNullOrWhiteSpace(vm.Position) ? "-" : vm.Position.Trim(),
                    Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim(),
                    OfficeId = vm.OfficeId,
                    IsFlexi = vm.IsFlexi,
                    IsActive = true,
                    Status = "ACTIVE",
                    CreatedDate = DateTime.UtcNow,
                    LastModifiedDate = DateTime.UtcNow,
                    ModifiedBy = actor
                };

                db.Employees.Add(emp);
                
                try
                {
                    db.SaveChanges();
                }
                catch (System.Data.Entity.Validation.DbEntityValidationException ex)
                {
                    var errors = ex.EntityValidationErrors
                        .SelectMany(e => e.ValidationErrors)
                        .Select(e => $"{e.PropertyName}: {e.ErrorMessage}");
                    
                    ModelState.AddModelError("", "Validation error: " + string.Join("; ", errors));
                    SetOffices(db, vm.OfficeId);
                    return View(vm);
                }
                
                DeviceService.SetEmployeeStatus(db, emp.Id, "ACTIVE", actor);

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionEmployeeCreate,
                    "Employee",
                    emp.EmployeeId,
                    "Gumawa ng bagong employee record.",
                    null,
                    new
                    {
                        emp.EmployeeId,
                        emp.FirstName,
                        emp.MiddleName,
                        emp.LastName,
                        emp.Position,
                        emp.Department,
                        emp.OfficeId,
                        emp.IsFlexi,
                        emp.IsActive,
                        Status = "ACTIVE"
                    });

                return RedirectToAction("Enroll", new { id = emp.Id });
            }
        }

        [HttpGet]
        public ActionResult Edit(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.FirstOrDefault(e => e.Id == id);
                if (emp == null) return HttpNotFound();

                SetOffices(db, emp.OfficeId);

                var vm = new EmployeeEditVm
                {
                    Id = emp.Id,
                    EmployeeId = emp.EmployeeId,
                    FirstName = emp.FirstName,
                    MiddleName = emp.MiddleName,
                    LastName = emp.LastName,
                    Position = emp.Position,
                    Department = emp.Department,
                    OfficeId = emp.OfficeId,
                    IsFlexi = emp.IsFlexi,
                    IsActive = emp.IsActive,
                    Status = DeviceService.GetEmployeeStatus(db, emp.Id)
                };

                return View(vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit(int id, EmployeeEditVm vm)
        {
            vm.EmployeeId = (vm.EmployeeId ?? "").Trim().ToUpperInvariant();
            vm.FirstName = (vm.FirstName ?? "").Trim();
            vm.MiddleName = (vm.MiddleName ?? "").Trim();
            vm.LastName = (vm.LastName ?? "").Trim();
            vm.Position = (vm.Position ?? "").Trim();
            vm.Status = NormalizeStatus(vm.Status);
            vm.IsActive = string.Equals(vm.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);

            using (var db = new FaceAttendDBEntities())
            {
                if (!ModelState.IsValid)
                {
                    SetOffices(db, vm.OfficeId);
                    return View(vm);
                }

                var emp = db.Employees.FirstOrDefault(e => e.Id == id);
                if (emp == null) return HttpNotFound();

                if (db.Employees.Any(e => e.EmployeeId == vm.EmployeeId && e.Id != id))
                {
                    ModelState.AddModelError("EmployeeId", "EmployeeId already exists.");
                    SetOffices(db, vm.OfficeId);
                    return View(vm);
                }

                var oldStatus = DeviceService.GetEmployeeStatus(db, emp.Id);
                var oldValues = new
                {
                    emp.EmployeeId,
                    emp.FirstName,
                    emp.MiddleName,
                    emp.LastName,
                    emp.Position,
                    emp.Department,
                    emp.OfficeId,
                    emp.IsFlexi,
                    emp.IsActive,
                    Status = oldStatus
                };

                emp.EmployeeId = vm.EmployeeId;
                emp.FirstName = vm.FirstName;
                emp.MiddleName = string.IsNullOrWhiteSpace(vm.MiddleName) ? null : vm.MiddleName;
                emp.LastName = vm.LastName;
                emp.Position = string.IsNullOrWhiteSpace(vm.Position) ? "-" : vm.Position.Trim();
                emp.Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim();
                emp.OfficeId = vm.OfficeId;
                emp.IsFlexi = vm.IsFlexi;
                emp.IsActive = vm.IsActive;
                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = AuditHelper.GetActorIp(Request);
                
                try
                {
                    db.SaveChanges();
                }
                catch (System.Data.Entity.Validation.DbEntityValidationException ex)
                {
                    var errors = ex.EntityValidationErrors
                        .SelectMany(e => e.ValidationErrors)
                        .Select(e => $"{e.PropertyName}: {e.ErrorMessage}");
                    
                    ModelState.AddModelError("", "Validation error: " + string.Join("; ", errors));
                    SetOffices(db, vm.OfficeId);
                    return View(vm);
                }

                var adminId = AdminAuthorizeAttribute.GetAdminId(Session);
                if (string.Equals(oldStatus, "PENDING", StringComparison.OrdinalIgnoreCase) && string.Equals(vm.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    var approveResult = DeviceService.ApprovePendingEmployee(db, emp.Id, adminId);
                    if (!approveResult.Success)
                    {
                        TempData["Error"] = approveResult.Message;
                    }
                    else
                    {
                        TempData["Success"] = approveResult.Message;
                    }
                }
                else if (string.Equals(oldStatus, "PENDING", StringComparison.OrdinalIgnoreCase) && string.Equals(vm.Status, "INACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    var rejectResult = DeviceService.RejectPendingEmployee(db, emp.Id, adminId, "Set to inactive by admin edit.");
                    if (!rejectResult.Success)
                    {
                        TempData["Error"] = rejectResult.Message;
                    }
                    else
                    {
                        TempData["Success"] = rejectResult.Message;
                    }
                }
                else
                {
                    DeviceService.SetEmployeeStatus(db, emp.Id, vm.Status, AuditHelper.GetActorIp(Request));
                    TempData["Success"] = "Employee updated successfully.";
                }

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionEmployeeEdit,
                    "Employee",
                    emp.EmployeeId,
                    "Nag-update ng employee record.",
                    oldValues,
                    new
                    {
                        emp.EmployeeId,
                        emp.FirstName,
                        emp.MiddleName,
                        emp.LastName,
                        emp.Position,
                        emp.Department,
                        emp.OfficeId,
                        emp.IsFlexi,
                        emp.IsActive,
                        Status = vm.Status
                    });

                // FIX: Ensure face matcher is fully reloaded after any employee edit
                EmployeeFaceIndex.Invalidate();
                FastFaceMatcher.ReloadFromDatabase();
                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();

                return RedirectToAction("Index", new { status = vm.Status == "PENDING" ? "PENDING" : "ALL" });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ApprovePendingEmployee(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var employee = db.Employees.FirstOrDefault(e => e.Id == id);
                if (employee == null)
                {
                    return Json(new { ok = false, message = "Employee not found." }, JsonRequestBehavior.AllowGet);
                }

                var adminId = AdminAuthorizeAttribute.GetAdminId(Session);
                var result = DeviceService.ApprovePendingEmployee(db, id, adminId);

                if (result.Success)
                {
                    // FIX: Ensure face matcher cache is fully reloaded after approval
                    EmployeeFaceIndex.Invalidate();
                    FastFaceMatcher.ReloadFromDatabase();
                    // Also ensure matcher is initialized
                    if (!FastFaceMatcher.IsInitialized)
                        FastFaceMatcher.Initialize();
                    return Json(new { ok = true, message = result.Message }, JsonRequestBehavior.AllowGet);
                }
                else
                {
                    return Json(new { ok = false, message = result.Message }, JsonRequestBehavior.AllowGet);
                }
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Delete(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.FirstOrDefault(e => e.Id == id);
                if (emp == null) return HttpNotFound();

                var oldStatus = DeviceService.GetEmployeeStatus(db, emp.Id);
                emp.IsActive = false;
                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = AuditHelper.GetActorIp(Request);
                db.SaveChanges();
                DeviceService.SetEmployeeStatus(db, emp.Id, "INACTIVE", AuditHelper.GetActorIp(Request));

                AuditHelper.Log(
                    db,
                    Request,
                    "EMPLOYEE_DEACTIVATE",
                    "Employee",
                    emp.EmployeeId,
                    $"Set employee inactive: {emp.FirstName} {emp.LastName}",
                    new { Status = oldStatus, emp.IsActive },
                    new { Status = "INACTIVE", IsActive = false });

                EmployeeFaceIndex.Invalidate();
                FastFaceMatcher.ReloadFromDatabase();

                return RedirectToAction("Index", new { status = "ALL" });
            }
        }

        [HttpGet]
        public ActionResult Enroll(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.FirstOrDefault(e => e.Id == id);
                if (emp == null) return HttpNotFound();
                var office = db.Offices.FirstOrDefault(o => o.Id == emp.OfficeId);
                ViewBag.OfficeName = office != null ? office.Name : "-";

                var perFrame = ConfigurationService.GetDouble(db, "Biometrics:LivenessThreshold", 0.75);
                ViewBag.PerFrame = perFrame.ToString("0.00####", CultureInfo.InvariantCulture);

                return View(emp);
            }
        }

        private void SetOffices(FaceAttendDBEntities db, int? selectedOfficeId)
        {
            ViewBag.Offices = new SelectList(
                db.Offices.Where(o => o.IsActive).OrderBy(o => o.Name).ToList(),
                "Id",
                "Name",
                selectedOfficeId
            );
        }

        public ActionResult PendingDevices()
        {
            using (var db = new FaceAttendDBEntities())
            {
                var devices = db.Devices
                    .Where(d => d.Status == "PENDING")
                    .Include(d => d.Employee)
                    .OrderByDescending(d => d.RegisteredAt)
                    .ToList();

                return View(devices);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult ApproveDevice(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var adminId = AdminAuthorizeAttribute.GetAdminId(Session);
                var result = DeviceService.ApproveDevice(db, id, adminId);

                if (result.Success)
                    TempData["Success"] = result.Message ?? "Device approved.";
                else
                    TempData["Error"] = result.Message ?? "Failed to approve device.";

                return RedirectToAction("PendingDevices");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult RejectDevice(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var device = db.Devices.Find(id);
                if (device == null)
                    return HttpNotFound();

                device.Status = "BLOCKED";
                db.SaveChanges();

                TempData["Success"] = "Device rejected.";
                return RedirectToAction("PendingDevices");
            }
        }

        public ActionResult Devices()
        {
            using (var db = new FaceAttendDBEntities())
            {
                var devices = db.Devices
                    .Include(d => d.Employee)
                    .OrderByDescending(d => d.RegisteredAt)
                    .ToList();

                return View(devices);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult DisableDevice(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var device = db.Devices.Find(id);
                if (device == null)
                    return HttpNotFound();

                device.Status = "BLOCKED";
                db.SaveChanges();

                AuditHelper.Log(db, Request, "DEVICE_DISABLED", "Device",
                    device.Id.ToString(),
                    $"Device disabled for employee {device.EmployeeId}",
                    null, null);

                TempData["Success"] = "Device disabled.";
                return RedirectToAction("Devices");
            }
        }

        private List<EmployeeListRowVm> QueryEmployeeRows(FaceAttendDBEntities db, string q, string status)
        {
            var term = (q ?? "").Trim();
            var like = "%" + term + "%";

            return db.Database.SqlQuery<EmployeeListRowVm>(@"
SELECT e.Id,
       e.EmployeeId,
       e.FirstName,
       e.MiddleName,
       e.LastName,
       e.Position,
       e.Department,
       e.OfficeId,
       ISNULL(o.Name, '-') AS OfficeName,
       e.IsFlexi,
       e.IsActive,
       CASE WHEN e.FaceEncodingBase64 IS NOT NULL OR e.FaceEncodingsJson IS NOT NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS HasFace,
       ISNULL(e.[Status], CASE WHEN e.IsActive = 1 THEN 'ACTIVE' ELSE 'INACTIVE' END) AS [Status],
       e.CreatedDate
FROM dbo.Employees e
LEFT JOIN dbo.Offices o ON o.Id = e.OfficeId
WHERE (@term = ''
       OR e.EmployeeId LIKE @like
       OR e.FirstName LIKE @like
       OR e.LastName LIKE @like
       OR ISNULL(e.MiddleName, '') LIKE @like
       OR ISNULL(e.Department, '') LIKE @like
       OR ISNULL(e.Position, '') LIKE @like)
  AND (@status = 'ALL'
       OR ISNULL(e.[Status], CASE WHEN e.IsActive = 1 THEN 'ACTIVE' ELSE 'INACTIVE' END) = @status)
ORDER BY CASE WHEN ISNULL(e.[Status], CASE WHEN e.IsActive = 1 THEN 'ACTIVE' ELSE 'INACTIVE' END) = 'PENDING' THEN 0 ELSE 1 END,
         e.LastName,
         e.FirstName,
         e.EmployeeId",
                new SqlParameter("@term", term),
                new SqlParameter("@like", like),
                new SqlParameter("@status", status)).ToList();
        }

        private string NormalizeStatus(string status)
        {
            var normalized = (status ?? "ACTIVE").Trim().ToUpperInvariant();
            if (normalized != "ACTIVE" && normalized != "PENDING" && normalized != "INACTIVE" && normalized != "ALL")
            {
                normalized = "ACTIVE";
            }
            return normalized;
        }
    }

    public class EmployeeListRowVm
    {
        public int Id { get; set; }
        public string EmployeeId { get; set; }
        public string FirstName { get; set; }
        public string MiddleName { get; set; }
        public string LastName { get; set; }
        public string Position { get; set; }
        public string Department { get; set; }
        public int OfficeId { get; set; }
        public string OfficeName { get; set; }
        public bool IsFlexi { get; set; }
        public bool IsActive { get; set; }
        public bool HasFace { get; set; }
        public string Status { get; set; }
        public DateTime CreatedDate { get; set; }
    }

}
