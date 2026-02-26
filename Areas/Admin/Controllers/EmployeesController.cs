using System;
using System.Data.Entity;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;
using FaceAttend.Services;
using FaceAttend.Services.Biometrics;
using System.Globalization;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class EmployeesController : Controller
    {
        public ActionResult Index(string q, int? page, bool? showInactive)
        {
            using (var db = new FaceAttendDBEntities())
            {
                int pageSize = AppSettings.GetInt("Employees:PageSize", 200);
                if (pageSize < 25) pageSize = 25;
                if (pageSize > 1000) pageSize = 1000;

                int p = page.GetValueOrDefault(1);
                if (p < 1) p = 1;

                bool includeInactive = showInactive.HasValue && showInactive.Value;

                var baseQ = db.Employees.AsNoTracking().AsQueryable();

                if (!includeInactive)
                    baseQ = baseQ.Where(e => e.IsActive);

                if (!string.IsNullOrWhiteSpace(q))
                {
                    var term = q.Trim();
                    baseQ = baseQ.Where(e =>
                        e.EmployeeId.Contains(term) ||
                        e.FirstName.Contains(term) ||
                        e.LastName.Contains(term) ||
                        e.MiddleName.Contains(term) ||
                        e.Department.Contains(term) ||
                        e.Position.Contains(term));
                }

                var total = baseQ.Count();
                var totalPages = (int)Math.Ceiling(total / (double)pageSize);
                if (totalPages <= 0) totalPages = 1;
                if (p > totalPages) p = totalPages;

                var skip = Math.Max(0, (p - 1) * pageSize);

                // Avoid relying on EF navigation naming when there are multiple Office FKs.
                // Always use OfficeId as the employee office.
                var employees = baseQ
                    .OrderBy(e => e.EmployeeId)
                    .Skip(skip)
                    .Take(pageSize)
                    .ToList();

                var officeIds = employees.Select(e => e.OfficeId).Distinct().ToList();
                var offices = db.Offices.AsNoTracking()
                    .Where(o => officeIds.Contains(o.Id))
                    .ToList()
                    .ToDictionary(o => o.Id, o => o.Name);

                ViewBag.OfficeNames = offices;

                ViewBag.Q = q ?? "";
                ViewBag.Page = p;
                ViewBag.PageSize = pageSize;
                ViewBag.Total = total;
                ViewBag.ShowInactive = includeInactive;

                return View(employees);
            }
        }

        [HttpGet]
        public ActionResult Create()
        {
            using (var db = new FaceAttendDBEntities())
            {
                SetOffices(db, null);
                return View(new EmployeeEditVm());
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

                var emp = new Employee
                {
                    EmployeeId = vm.EmployeeId,
                    FirstName = vm.FirstName,
                    MiddleName = string.IsNullOrWhiteSpace(vm.MiddleName) ? null : vm.MiddleName,
                    LastName = vm.LastName,
                    Position = string.IsNullOrWhiteSpace(vm.Position) ? null : vm.Position,
                    Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim(),
                    OfficeId = vm.OfficeId,
                    IsFlexi = vm.IsFlexi,
                    IsActive = vm.IsActive,
                    LastModifiedDate = DateTime.UtcNow,
                    ModifiedBy = "ADMIN"
                };

                db.Employees.Add(emp);
                db.SaveChanges();

                // Wizard flow: go straight to face enrollment
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
                    IsActive = emp.IsActive
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

                emp.EmployeeId = vm.EmployeeId;
                emp.FirstName = vm.FirstName;
                emp.MiddleName = string.IsNullOrWhiteSpace(vm.MiddleName) ? null : vm.MiddleName;
                emp.LastName = vm.LastName;
                emp.Position = string.IsNullOrWhiteSpace(vm.Position) ? null : vm.Position;
                emp.Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim();
                emp.OfficeId = vm.OfficeId;
                emp.IsFlexi = vm.IsFlexi;
                emp.IsActive = vm.IsActive;
                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = "ADMIN";

                db.SaveChanges();

                // EmployeeId and IsActive can affect the in-memory index.
                EmployeeFaceIndex.Invalidate();

                return RedirectToAction("Index");
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

                emp.IsActive = false;
                emp.LastModifiedDate = DateTime.UtcNow;
                emp.ModifiedBy = "ADMIN";

                db.SaveChanges();

                EmployeeFaceIndex.Invalidate();

                return RedirectToAction("Index");
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

                var perFrame = SystemConfigService.GetDouble(db, "Biometrics:LivenessThreshold", 0.75);
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
