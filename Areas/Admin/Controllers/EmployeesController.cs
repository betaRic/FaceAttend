using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Helpers;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Models.Dtos;
using FaceAttend.Filters;
using FaceAttend.Infrastructure;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    [RateLimit(Name = "AdminEmployees", MaxRequests = 120, WindowSeconds = 60, Burst = 30)]
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

                var normalizedStatus = EmployeeQueryHelper.NormalizeStatus(status);
                var allRows = EmployeeQueryHelper.QueryRows(db, q, normalizedStatus);
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
                    Status = "ACTIVE"
                });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "AdminEmployeeMutate", MaxRequests = 30, WindowSeconds = 60, Burst = 5)]
        public ActionResult Create(EmployeeEditVm vm)
        {
            vm.EmployeeId = (vm.EmployeeId ?? "").Trim().ToUpperInvariant();
            vm.FirstName = (vm.FirstName ?? "").Trim();
            vm.MiddleName = (vm.MiddleName ?? "").Trim();
            vm.LastName = (vm.LastName ?? "").Trim();
            vm.Position = (vm.Position ?? "").Trim();
            vm.Status = "ACTIVE";

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
                    // Store null for blank Position — consistent with MiddleName and Department.
                    Position = string.IsNullOrWhiteSpace(vm.Position) ? null : vm.Position.Trim(),
                    Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim(),
                    OfficeId = vm.OfficeId,
                    IsFlexi = vm.IsFlexi,
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
                    ModelState.AddDbValidationErrors(ex);
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

                    Status = DeviceService.GetEmployeeStatus(db, emp.Id)
                };

                return View(vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "AdminEmployeeMutate", MaxRequests = 30, WindowSeconds = 60, Burst = 5)]
        public ActionResult Edit(int id, EmployeeEditVm vm)
        {
            vm.EmployeeId = (vm.EmployeeId ?? "").Trim().ToUpperInvariant();
            vm.FirstName = (vm.FirstName ?? "").Trim();
            vm.MiddleName = (vm.MiddleName ?? "").Trim();
            vm.LastName = (vm.LastName ?? "").Trim();
            vm.Position = (vm.Position ?? "").Trim();
            vm.Status = EmployeeQueryHelper.NormalizeStatus(vm.Status);


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
                    Status = oldStatus
                };

                emp.EmployeeId = vm.EmployeeId;
                emp.FirstName = vm.FirstName;
                emp.MiddleName = string.IsNullOrWhiteSpace(vm.MiddleName) ? null : vm.MiddleName;
                emp.LastName = vm.LastName;
                // Store null for blank Position — consistent with Create action.
                emp.Position = string.IsNullOrWhiteSpace(vm.Position) ? null : vm.Position.Trim();
                emp.Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim();
                emp.OfficeId = vm.OfficeId;
                emp.IsFlexi = vm.IsFlexi;

                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = AuditHelper.GetActorIp(Request);
                
                try
                {
                    db.SaveChanges();
                }
                catch (System.Data.Entity.Validation.DbEntityValidationException ex)
                {
                    ModelState.AddDbValidationErrors(ex);
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
                        Status = vm.Status
                    });

                // Reload face matcher so changes take effect immediately.
                FastFaceMatcher.ReloadFromDatabase();
                if (!FastFaceMatcher.IsInitialized)
                    FastFaceMatcher.Initialize();

                return RedirectToAction("Index", new { status = vm.Status == "PENDING" ? "PENDING" : "ALL" });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RateLimit(Name = "AdminEmployeeApproval", MaxRequests = 20, WindowSeconds = 60, Burst = 5)]
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
                    // Reload face matcher so the approved employee can be recognized.
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
        [RateLimit(Name = "AdminEmployeeMutate", MaxRequests = 30, WindowSeconds = 60, Burst = 5)]
        public ActionResult Delete(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var emp = db.Employees.FirstOrDefault(e => e.Id == id);
                if (emp == null) return HttpNotFound();

                var oldStatus = DeviceService.GetEmployeeStatus(db, emp.Id);

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
                    new { Status = oldStatus },
                    new { Status = "INACTIVE" });

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

                var perFrame = BiometricPolicy.Current.AntiSpoofClearThreshold;
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

    }
}
