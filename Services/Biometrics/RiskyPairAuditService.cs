using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceAttend.Services.Biometrics
{
    public static class RiskyPairAuditService
    {
        public sealed class AuditResult
        {
            public DateTime GeneratedAtUtc { get; set; }
            public int EmployeeCount { get; set; }
            public int VectorCount { get; set; }
            public int UnsafePairCount { get; set; }
            public double CriticalDistance { get; set; }
            public double UnsafeDistance { get; set; }
            public double WatchDistance { get; set; }
            public IList<RiskyPairRow> Rows { get; set; } = new List<RiskyPairRow>();
        }

        public sealed class RiskyPairRow
        {
            public string EmployeeId { get; set; }
            public string DisplayName { get; set; }
            public string OtherEmployeeId { get; set; }
            public string OtherDisplayName { get; set; }
            public double Distance { get; set; }
            public int VectorIndex { get; set; }
            public int OtherVectorIndex { get; set; }
            public string RiskLevel { get; set; }
            public string Recommendation { get; set; }
        }

        private sealed class VectorRow
        {
            public FaceEncodingHelper.EmployeeFaceData Employee { get; set; }
            public double[] Vector { get; set; }
            public int VectorIndex { get; set; }
        }

        public static AuditResult Analyze(FaceAttendDBEntities db, int maxRows)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var maxPerEmployee = ConfigurationService.GetInt("Biometrics:Enroll:MaxStoredVectors", 25);
            var employees = FaceEncodingHelper.LoadAllEmployeeFaces(db, maxPerEmployee);
            var vectors = employees
                .SelectMany(e => e.FaceVectors.Select((v, i) => new VectorRow
                {
                    Employee = e,
                    Vector = v,
                    VectorIndex = i
                }))
                .Where(x => FaceVectorCodec.IsValidVector(x.Vector))
                .ToList();

            var bestByEmployee = new Dictionary<string, RiskyPairRow>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < vectors.Count; i++)
            {
                var a = vectors[i];
                for (var j = i + 1; j < vectors.Count; j++)
                {
                    var b = vectors[j];
                    if (string.Equals(a.Employee.EmployeeId, b.Employee.EmployeeId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dist = FaceVectorCodec.Distance(a.Vector, b.Vector);
                    UpdateBest(bestByEmployee, a, b, dist);
                    UpdateBest(bestByEmployee, b, a, dist);
                }
            }

            var critical = ConfigurationService.GetDouble(
                "Biometrics:RiskAudit:CriticalDistance",
                FastFaceMatcher.HighDistThresholdPublic);
            var unsafeDistance = ConfigurationService.GetDouble(
                "Biometrics:RiskAudit:UnsafeDistance",
                FastFaceMatcher.MedDistThresholdPublic);
            var watch = ConfigurationService.GetDouble(
                "Biometrics:RiskAudit:WatchDistance",
                unsafeDistance + 0.05);

            var rows = bestByEmployee.Values
                .Select(r => Classify(r, critical, unsafeDistance, watch))
                .OrderBy(r => r.Distance)
                .ThenBy(r => r.EmployeeId)
                .Take(Math.Max(1, maxRows))
                .ToList();

            return new AuditResult
            {
                GeneratedAtUtc = DateTime.UtcNow,
                EmployeeCount = employees.Count,
                VectorCount = vectors.Count,
                CriticalDistance = critical,
                UnsafeDistance = unsafeDistance,
                WatchDistance = watch,
                UnsafePairCount = rows.Count(r => r.RiskLevel == "CRITICAL" || r.RiskLevel == "HIGH"),
                Rows = rows
            };
        }

        private static void UpdateBest(
            IDictionary<string, RiskyPairRow> bestByEmployee,
            VectorRow subject,
            VectorRow other,
            double distance)
        {
            RiskyPairRow existing;
            if (bestByEmployee.TryGetValue(subject.Employee.EmployeeId, out existing) &&
                existing.Distance <= distance)
                return;

            bestByEmployee[subject.Employee.EmployeeId] = new RiskyPairRow
            {
                EmployeeId = subject.Employee.EmployeeId,
                DisplayName = subject.Employee.DisplayName,
                OtherEmployeeId = other.Employee.EmployeeId,
                OtherDisplayName = other.Employee.DisplayName,
                Distance = distance,
                VectorIndex = subject.VectorIndex,
                OtherVectorIndex = other.VectorIndex
            };
        }

        private static RiskyPairRow Classify(
            RiskyPairRow row,
            double critical,
            double unsafeDistance,
            double watch)
        {
            if (row.Distance <= critical)
            {
                row.RiskLevel = "CRITICAL";
                row.Recommendation = "Block approval or re-enroll one employee before production use.";
            }
            else if (row.Distance <= unsafeDistance)
            {
                row.RiskLevel = "HIGH";
                row.Recommendation = "Require admin review and stricter threshold before auto-recording.";
            }
            else if (row.Distance <= watch)
            {
                row.RiskLevel = "WATCH";
                row.Recommendation = "Monitor in pilot and capture better enrollment samples if retries occur.";
            }
            else
            {
                row.RiskLevel = "OK";
                row.Recommendation = "No immediate action.";
            }

            return row;
        }
    }
}
