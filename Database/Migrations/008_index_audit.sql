-- FaceAttend — Index Audit and Creation
-- Plan 8 Phase F
-- Idempotent: safe to run multiple times
-- Run during off-hours on production — CREATE INDEX briefly locks the table.

PRINT 'FaceAttend Index Audit — starting';

-- 1. AttendanceLogs: EmployeeId + Timestamp
--    Used by AttendanceService.Record() on every scan attempt.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.AttendanceLogs')
      AND name = 'IX_AttendanceLogs_EmployeeId_Timestamp')
BEGIN
    PRINT 'Creating IX_AttendanceLogs_EmployeeId_Timestamp...';
    CREATE INDEX IX_AttendanceLogs_EmployeeId_Timestamp
    ON dbo.AttendanceLogs (EmployeeId, Timestamp DESC);
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_AttendanceLogs_EmployeeId_Timestamp already exists — skipped.';

-- 2. Devices: DeviceToken (filtered — excludes NULLs)
--    Used by DeviceService.ValidateDevice() on every mobile scan.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.Devices')
      AND name = 'IX_Devices_DeviceToken')
BEGIN
    PRINT 'Creating IX_Devices_DeviceToken...';
    CREATE INDEX IX_Devices_DeviceToken
    ON dbo.Devices (DeviceToken)
    WHERE DeviceToken IS NOT NULL;
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_Devices_DeviceToken already exists — skipped.';

-- 3. Devices: Fingerprint (filtered — excludes NULLs)
--    Used by DeviceService.ValidateDevice() on every mobile scan.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.Devices')
      AND name = 'IX_Devices_Fingerprint')
BEGIN
    PRINT 'Creating IX_Devices_Fingerprint...';
    CREATE INDEX IX_Devices_Fingerprint
    ON dbo.Devices (Fingerprint)
    WHERE Fingerprint IS NOT NULL;
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_Devices_Fingerprint already exists — skipped.';

-- 4. VisitorLogs: Timestamp
--    Used by VisitorService.PurgeOldLogs() — prevents full table scan on purge.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.VisitorLogs')
      AND name = 'IX_VisitorLogs_Timestamp')
BEGIN
    PRINT 'Creating IX_VisitorLogs_Timestamp...';
    CREATE INDEX IX_VisitorLogs_Timestamp
    ON dbo.VisitorLogs (Timestamp);
    PRINT 'Done.';
END
ELSE
    PRINT 'IX_VisitorLogs_Timestamp already exists — skipped.';

-- 5. SystemConfigurations: Key (unique)
--    Used by ConfigurationService.GetFromDb() on every cache miss.
--    Also enforces key uniqueness at DB level (currently app-only).
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.SystemConfigurations')
      AND name = 'UX_SystemConfigurations_Key')
BEGIN
    PRINT 'Creating UX_SystemConfigurations_Key...';
    CREATE UNIQUE INDEX UX_SystemConfigurations_Key
    ON dbo.SystemConfigurations ([Key]);
    PRINT 'Done.';
END
ELSE
    PRINT 'UX_SystemConfigurations_Key already exists — skipped.';

PRINT 'FaceAttend Index Audit — complete';
