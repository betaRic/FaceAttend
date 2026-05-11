using System;
using System.Collections.Generic;
using FaceAttend.Services;

namespace FaceAttend.Models.ViewModels.Admin
{
    public class OperationsVm
    {
        public bool DatabaseHealthy { get; set; }
        public string DatabaseError { get; set; }
        public DatabaseMigrationStatusService.Summary MigrationStatus { get; set; }
        public bool BiometricWorkerReady { get; set; }
        public int WarmUpState { get; set; }
        public string WarmUpMessage { get; set; }
        public string ModelVersion { get; set; }
        public bool ModelIntegrityOk { get; set; }
        public bool ModelHashesConfigured { get; set; }
        public bool ModelAclOk { get; set; }
        public bool ModelRequireReadOnlyAcl { get; set; }
        public bool WorkerEnabled { get; set; }
        public bool WorkerHealthy { get; set; }
        public string WorkerStatus { get; set; }
        public int? FaceMatcherCacheAgeSeconds { get; set; }
        public double? DiskFreeGb { get; set; }
        public long? TmpMb { get; set; }
        public object BiometricWorkerStatus { get; set; }
        public object FaceMatcherStats { get; set; }
        public OperationalMetricsService.Snapshot ScanMetrics { get; set; }
        public CalibrationSummaryService.Summary CalibrationSummary { get; set; }
        public int PendingReviews { get; set; }
        public DateTime ServerTimeLocal { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
