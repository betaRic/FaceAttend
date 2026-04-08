using System;
using System.Collections.Generic;
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
                Longitude = 125.171617,
                WorkDays = "1,2,3,4,5" // default Mon–Fri
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
                var normWork = OfficeScheduleService.NormalizeDayMask(
                    (vm.WorkDays ?? "").Split(',').Select(s => { int n; return int.TryParse(s.Trim(), out n) ? n : 0; }).Where(n => n > 0));
                var normWfh = OfficeScheduleService.NormalizeDayMask(
                    (vm.WfhDays ?? "").Split(',').Select(s => { int n; return int.TryParse(s.Trim(), out n) ? n : 0; }).Where(n => n > 0));

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
                    WorkDays = string.IsNullOrEmpty(normWork) ? null : normWork,
                    WfhDays = string.IsNullOrEmpty(normWfh) ? null : normWfh,
                    WfhEnabled = vm.WfhEnabled,
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
                        o.IsActive,
                        o.WorkDays,
                        o.WfhDays,
                        o.WfhEnabled
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
                    IsActive = o.IsActive,
                    WorkDays = string.IsNullOrWhiteSpace(o.WorkDays) ? "1,2,3,4,5" : o.WorkDays,
                    WfhDays = o.WfhDays,
                    WfhEnabled = o.WfhEnabled
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
                    o.IsActive,
                    o.WorkDays,
                    o.WfhDays,
                    o.WfhEnabled
                };

                var editNormWork = OfficeScheduleService.NormalizeDayMask(
                    (vm.WorkDays ?? "").Split(',').Select(s => { int n; return int.TryParse(s.Trim(), out n) ? n : 0; }).Where(n => n > 0));
                var editNormWfh = OfficeScheduleService.NormalizeDayMask(
                    (vm.WfhDays ?? "").Split(',').Select(s => { int n; return int.TryParse(s.Trim(), out n) ? n : 0; }).Where(n => n > 0));

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
                o.WorkDays = string.IsNullOrEmpty(editNormWork) ? null : editNormWork;
                o.WfhDays = string.IsNullOrEmpty(editNormWfh) ? null : editNormWfh;
                o.WfhEnabled = vm.WfhEnabled;

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
                        o.IsActive,
                        o.WorkDays,
                        o.WfhDays,
                        o.WfhEnabled
                    });

                return RedirectToAction("Index");
            }
        }

        // ── BulkSchedule ──────────────────────────────────────────────────────

        [HttpGet]
        public ActionResult BulkSchedule()
        {
            ViewBag.Title = "Office Schedules";
            using (var db = new FaceAttendDBEntities())
            {
                var offices = db.Offices.OrderBy(o => o.ProvinceName).ThenBy(o => o.Name).ToList();
                var vm = new OfficeBulkScheduleVm
                {
                    Offices = offices.Select(o => new OfficeBulkScheduleRowVm
                    {
                        Id           = o.Id,
                        Name         = o.Name,
                        ProvinceName = o.ProvinceName ?? o.HUCCity ?? "",
                        WfhEnabled   = o.WfhEnabled,
                        WorkDays     = MaskToBoolArray(o.WorkDays, isWork: true),
                        WfhDays      = MaskToBoolArray(o.WfhDays,  isWork: false)
                    }).ToList()
                };
                return View(vm);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult BulkSchedule(OfficeBulkScheduleVm vm)
        {
            if (vm == null || vm.Offices == null)
                return RedirectToAction("BulkSchedule");

            using (var db = new FaceAttendDBEntities())
            {
                var officeMap = db.Offices.ToDictionary(o => o.Id);
                var changes   = new List<object>();

                foreach (var row in vm.Offices)
                {
                    if (!officeMap.ContainsKey(row.Id)) continue;
                    var o = officeMap[row.Id];

                    var newWork = OfficeScheduleService.NormalizeDayMask(
                        BoolArrayToIsoList(row.WorkDays));
                    var newWfh = OfficeScheduleService.NormalizeDayMask(
                        BoolArrayToIsoList(row.WfhDays));

                    if (o.WorkDays != (string.IsNullOrEmpty(newWork) ? null : newWork)
                        || o.WfhDays != (string.IsNullOrEmpty(newWfh) ? null : newWfh)
                        || o.WfhEnabled != row.WfhEnabled)
                    {
                        changes.Add(new
                        {
                            row.Id,
                            o.Name,
                            OldWork = o.WorkDays,
                            NewWork = newWork,
                            OldWfh  = o.WfhDays,
                            NewWfh  = newWfh,
                            OldWfhEnabled = o.WfhEnabled,
                            NewWfhEnabled = row.WfhEnabled
                        });
                        o.WorkDays   = string.IsNullOrEmpty(newWork) ? null : newWork;
                        o.WfhDays    = string.IsNullOrEmpty(newWfh)  ? null : newWfh;
                        o.WfhEnabled = row.WfhEnabled;
                    }
                }

                db.SaveChanges();

                if (changes.Count > 0)
                {
                    AuditHelper.Log(
                        db, Request,
                        AuditHelper.ActionOfficeBulkSchedule,
                        "Office", 0,
                        "Bulk schedule update for " + changes.Count + " office(s).",
                        null,
                        changes);
                }

                TempData["msg"]     = "Schedules saved for " + changes.Count + " office(s).";
                TempData["msgKind"] = "success";
                return RedirectToAction("BulkSchedule");
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>Converts a day mask string to a 7-element bool array [0]=Mon…[6]=Sun.</summary>
        private static bool[] MaskToBoolArray(string mask, bool isWork)
        {
            var result = new bool[7];
            if (string.IsNullOrWhiteSpace(mask))
            {
                // Default: if isWork, check Mon–Fri; WFH days default to none
                if (isWork) { result[0]=result[1]=result[2]=result[3]=result[4]=true; }
                return result;
            }
            foreach (var part in mask.Split(','))
            {
                int iso;
                if (int.TryParse(part.Trim(), out iso) && iso >= 1 && iso <= 7)
                    result[iso - 1] = true;
            }
            return result;
        }

        /// <summary>Converts a 7-element bool array [0]=Mon…[6]=Sun to ISO day numbers.</summary>
        private static IEnumerable<int> BoolArrayToIsoList(bool[] arr)
        {
            if (arr == null) yield break;
            for (int i = 0; i < Math.Min(arr.Length, 7); i++)
                if (arr[i]) yield return i + 1;
        }
    }
}
