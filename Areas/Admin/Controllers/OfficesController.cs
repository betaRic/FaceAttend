using System;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Models.ViewModels.Admin;
using FaceAttend.Filters;
using FaceAttend.Services;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class OfficesController : Controller
    {
        public ActionResult Index()
        {
            using (var db = new FaceAttendDBEntities())
            {
                var list = db.Offices
                    .OrderBy(o => o.Name)
                    .ToList();

                ViewBag.Title = "Offices";
                return View(list);
            }
        }

        [HttpGet]
        public ActionResult Create()
        {
            ViewBag.Title = "Create Office";
            var vm = new OfficeEditVm
            {
                RadiusMeters = 100,
                IsActive = true,
                Latitude = 6.116386,   // Philippines center-ish fallback
                Longitude = 125.171617
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create(OfficeEditVm vm)
        {
            vm.Code = (vm.Code ?? "").Trim();
            vm.Name = (vm.Name ?? "").Trim();
            vm.Type = (vm.Type ?? "").Trim();
            vm.ProvinceName = (vm.ProvinceName ?? "").Trim();
            vm.HUCCity = (vm.HUCCity ?? "").Trim();
            vm.WiFiBSSID = (vm.WiFiBSSID ?? "").Trim();

            // server-side validation (do not rely on dropdown alone)
            var validTypes = new[] { "REGION", "PROVINCE", "HUC" };
            vm.Type = (vm.Type ?? "").Trim().ToUpperInvariant();
            if (!validTypes.Contains(vm.Type))
            {
                ModelState.AddModelError("Type", "Type must be REGION, PROVINCE, or HUC.");
            }

            if (!ModelState.IsValid)
            {
                ViewBag.Title = "Create Office";
                return View(vm);
            }

            using (var db = new FaceAttendDBEntities())
            {
                var o = new Office
                {
                    Code = string.IsNullOrWhiteSpace(vm.Code) ? null : vm.Code,
                    Name = vm.Name,
                    Type = string.IsNullOrWhiteSpace(vm.Type) ? null : vm.Type,
                    ProvinceName = string.IsNullOrWhiteSpace(vm.ProvinceName) ? null : vm.ProvinceName,
                    HUCCity = string.IsNullOrWhiteSpace(vm.HUCCity) ? null : vm.HUCCity,
                    Latitude = vm.Latitude,
                    Longitude = vm.Longitude,
                    RadiusMeters = vm.RadiusMeters,
                    WiFiBSSID = string.IsNullOrWhiteSpace(vm.WiFiBSSID) ? null : vm.WiFiBSSID,
                    IsActive = vm.IsActive,
                    CreatedDate = DateTime.UtcNow
                };

                db.Offices.Add(o);
                db.SaveChanges();

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionOfficeCreate,
                    "Office",
                    o.Id,
                    "Gumawa ng bagong office record.",
                    null,
                    new
                    {
                        o.Code,
                        o.Name,
                        o.Type,
                        o.ProvinceName,
                        o.HUCCity,
                        o.Latitude,
                        o.Longitude,
                        o.RadiusMeters,
                        o.WiFiBSSID,
                        o.IsActive
                    });

                return RedirectToAction("Index");
            }
        }

        [HttpGet]
        public ActionResult Edit(int id)
        {
            using (var db = new FaceAttendDBEntities())
            {
                var o = db.Offices.FirstOrDefault(x => x.Id == id);
                if (o == null) return HttpNotFound();

                ViewBag.Title = "Edit Office";
                var vm = new OfficeEditVm
                {
                    Id = o.Id,
                    Code = o.Code,
                    Name = o.Name,
                    Type = o.Type,
                    ProvinceName = o.ProvinceName,
                    HUCCity = o.HUCCity,
                    Latitude = o.Latitude,
                    Longitude = o.Longitude,
                    RadiusMeters = o.RadiusMeters > 0 ? o.RadiusMeters : 100,
                    WiFiBSSID = o.WiFiBSSID,
                    IsActive = o.IsActive
                };

                return View(vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit(int id, OfficeEditVm vm)
        {
            vm.Code = (vm.Code ?? "").Trim();
            vm.Name = (vm.Name ?? "").Trim();
            vm.Type = (vm.Type ?? "").Trim();
            vm.ProvinceName = (vm.ProvinceName ?? "").Trim();
            vm.HUCCity = (vm.HUCCity ?? "").Trim();
            vm.WiFiBSSID = (vm.WiFiBSSID ?? "").Trim();

            // server-side validation (do not rely on dropdown alone)
            var validTypes = new[] { "REGION", "PROVINCE", "HUC" };
            vm.Type = (vm.Type ?? "").Trim().ToUpperInvariant();
            if (!validTypes.Contains(vm.Type))
            {
                ModelState.AddModelError("Type", "Type must be REGION, PROVINCE, or HUC.");
            }

            if (!ModelState.IsValid)
            {
                ViewBag.Title = "Edit Office";
                return View(vm);
            }

            using (var db = new FaceAttendDBEntities())
            {
                var o = db.Offices.FirstOrDefault(x => x.Id == id);
                if (o == null) return HttpNotFound();

                var oldValues = new
                {
                    o.Code,
                    o.Name,
                    o.Type,
                    o.ProvinceName,
                    o.HUCCity,
                    o.Latitude,
                    o.Longitude,
                    o.RadiusMeters,
                    o.WiFiBSSID,
                    o.IsActive
                };

                o.Code = string.IsNullOrWhiteSpace(vm.Code) ? null : vm.Code;
                o.Name = vm.Name;
                o.Type = string.IsNullOrWhiteSpace(vm.Type) ? null : vm.Type;
                o.ProvinceName = string.IsNullOrWhiteSpace(vm.ProvinceName) ? null : vm.ProvinceName;
                o.HUCCity = string.IsNullOrWhiteSpace(vm.HUCCity) ? null : vm.HUCCity;
                o.Latitude = vm.Latitude;
                o.Longitude = vm.Longitude;
                o.RadiusMeters = vm.RadiusMeters;
                o.WiFiBSSID = string.IsNullOrWhiteSpace(vm.WiFiBSSID) ? null : vm.WiFiBSSID;
                o.IsActive = vm.IsActive;

                db.SaveChanges();

                AuditHelper.Log(
                    db,
                    Request,
                    AuditHelper.ActionOfficeEdit,
                    "Office",
                    o.Id,
                    "Nag-update ng office record.",
                    oldValues,
                    new
                    {
                        o.Code,
                        o.Name,
                        o.Type,
                        o.ProvinceName,
                        o.HUCCity,
                        o.Latitude,
                        o.Longitude,
                        o.RadiusMeters,
                        o.WiFiBSSID,
                        o.IsActive
                    });

                return RedirectToAction("Index");
            }
        }
    }
}
