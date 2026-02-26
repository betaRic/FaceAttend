using System;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Areas.Admin.Models;
using FaceAttend.Filters;

namespace FaceAttend.Areas.Admin.Controllers
{
    [AdminAuthorize]
    public class OfficesController : Controller
    {
        [HttpGet]
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
            vm.WiFiSSID = (vm.WiFiSSID ?? "").Trim();

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
                    WiFiSSID = string.IsNullOrWhiteSpace(vm.WiFiSSID) ? null : vm.WiFiSSID,
                    IsActive = vm.IsActive,
                    CreatedDate = DateTime.UtcNow
                };

                db.Offices.Add(o);
                db.SaveChanges();

                TempData["msg"] = "Office created.";
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
                    WiFiSSID = o.WiFiSSID,
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
            vm.WiFiSSID = (vm.WiFiSSID ?? "").Trim();

            if (!ModelState.IsValid)
            {
                ViewBag.Title = "Edit Office";
                return View(vm);
            }

            using (var db = new FaceAttendDBEntities())
            {
                var o = db.Offices.FirstOrDefault(x => x.Id == id);
                if (o == null) return HttpNotFound();

                o.Code = string.IsNullOrWhiteSpace(vm.Code) ? null : vm.Code;
                o.Name = vm.Name;
                o.Type = string.IsNullOrWhiteSpace(vm.Type) ? null : vm.Type;
                o.ProvinceName = string.IsNullOrWhiteSpace(vm.ProvinceName) ? null : vm.ProvinceName;
                o.HUCCity = string.IsNullOrWhiteSpace(vm.HUCCity) ? null : vm.HUCCity;
                o.Latitude = vm.Latitude;
                o.Longitude = vm.Longitude;
                o.RadiusMeters = vm.RadiusMeters;
                o.WiFiSSID = string.IsNullOrWhiteSpace(vm.WiFiSSID) ? null : vm.WiFiSSID;
                o.IsActive = vm.IsActive;

                db.SaveChanges();

                TempData["msg"] = "Office updated.";
                return RedirectToAction("Index");
            }
        }
    }
}
