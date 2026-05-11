using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using FaceAttend.Services;

namespace FaceAttend.Areas.Admin.Helpers
{
    public static class AdminQueryHelper
    {
        public static List<SelectListItem> BuildOfficeOptions(
            FaceAttendDBEntities db, int? selected)
        {
            var list = new List<SelectListItem>
            {
                new SelectListItem { Text = "All offices", Value = "" }
            };

            db.Offices.AsNoTracking()
              .Where(o => o.IsActive)
              .OrderBy(o => o.Name)
              .ToList()
              .ForEach(o => list.Add(new SelectListItem
              {
                  Text     = o.Name,
                  Value    = o.Id.ToString(),
                  Selected = selected.HasValue && selected.Value == o.Id
              }));

            return list;
        }

        public static List<SelectListItem> BuildOfficeOptionsWithAuto(
                    FaceAttendDBEntities db, int selected)
                {
                    var list = new List<SelectListItem>
                    {
                        new SelectListItem
                        {
                            Text     = "Auto (first active office)",
                            Value    = "0",
                            Selected = selected <= 0
                        }
                    };
        
                    db.Offices.AsNoTracking()
                      .Where(o => o.IsActive)
                      .OrderBy(o => o.Name)
                      .ToList()
                      .ForEach(o => list.Add(new SelectListItem
                      {
                          Text     = o.Name,
                          Value    = o.Id.ToString(),
                          Selected = selected == o.Id
                      }));
        
                    return list;
                }


        public class RangeResult
        {
            public DateTime? FromUtc { get; set; }
            public DateTime? ToUtcExclusive { get; set; }
            public DateTime? FromLocalInclusive { get; set; }
            public DateTime? ToLocalExclusive { get; set; }
            public DateTime FromLocalDate { get; set; }
            public DateTime ToLocalDate { get; set; }
            public string FromText { get; set; }
            public string ToText { get; set; }
            public string Label { get; set; }
        }

        public static RangeResult ParseRange(string from, string to)
        {
            var today = TimeZoneHelper.TodayLocalDate();
            var defaultFrom = today.AddDays(-6);
            var defaultTo = today;

            DateTime fromLocal;
            DateTime toLocal;

            switch ((from ?? "").Trim().ToLowerInvariant())
            {
                case "today":
                    fromLocal = today;
                    toLocal = today;
                    break;

                case "thisweek":
                    int dow1 = (int)today.DayOfWeek;
                    int offset1 = dow1 == 0 ? -6 : -(dow1 - 1);
                    fromLocal = today.AddDays(offset1);
                    toLocal = today;
                    break;

                case "lastweek":
                    int dow2 = (int)today.DayOfWeek;
                    int offset2 = dow2 == 0 ? -6 : -(dow2 - 1);
                    toLocal = today.AddDays(offset2 - 1);
                    fromLocal = toLocal.AddDays(-6);
                    break;

                case "thismonth":
                    fromLocal = new DateTime(today.Year, today.Month, 1);
                    toLocal = today;
                    break;

                default:
                    if (!DateTime.TryParse(from, out fromLocal)) fromLocal = defaultFrom;
                    fromLocal = fromLocal.Date;

                    if (!DateTime.TryParse(to, out toLocal)) toLocal = defaultTo;
                    toLocal = toLocal.Date;
                    break;
            }

            if (toLocal < fromLocal)
            {
                var tmp = fromLocal;
                fromLocal = toLocal;
                toLocal = tmp;
            }

            var localRange = TimeZoneHelper.LocalDateRange(fromLocal);
            var localEndRange = TimeZoneHelper.LocalDateRange(toLocal);
            var utcRange = TimeZoneHelper.LocalDateToUtcRange(fromLocal);
            var utcEndRange = TimeZoneHelper.LocalDateToUtcRange(toLocal);

            var label = fromLocal == toLocal
                ? fromLocal.ToString("MMM d, yyyy")
                : fromLocal.ToString("MMM d") + " – " + toLocal.ToString("MMM d, yyyy");

            return new RangeResult
            {
                FromUtc = utcRange.fromUtc,
                ToUtcExclusive = utcEndRange.toUtcExclusive,
                FromLocalInclusive = localRange.fromLocalInclusive,
                ToLocalExclusive = localEndRange.toLocalExclusive,
                FromLocalDate = fromLocal,
                ToLocalDate = toLocal,
                FromText = fromLocal.ToString("yyyy-MM-dd"),
                ToText = toLocal.ToString("yyyy-MM-dd"),
                Label = label
            };
        }
    }
}
