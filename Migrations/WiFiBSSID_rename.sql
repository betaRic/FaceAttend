/*
============================================================
  FaceAttend - WiFiBSSID Column Rename Migration
  Run in SQL Server Management Studio (SSMS)
  Target DB: FaceAttendDB
============================================================

  BEFORE RUNNING:
  1. Stop IIS / take the app offline
  2. Take a full backup first (line below — uncomment and run separately)
  3. Run this script in SSMS connected to the correct SQL Server instance
  4. Verify results at the bottom before bringing app back online

  BACKUP COMMAND (run separately before this script):
  BACKUP DATABASE FaceAttendDB
    TO DISK = N'C:\Backup\FaceAttendDB_pre_bssid_rename.bak'
    WITH FORMAT, INIT, NAME = 'FaceAttendDB-PreBSSIDRename';
============================================================
*/

USE FaceAttendDB;
GO

-- ============================================================
-- SAFETY CHECK: confirm we are on the right database
-- ============================================================
IF DB_NAME() != 'FaceAttendDB'
BEGIN
    RAISERROR('Wrong database! Connect to FaceAttendDB before running.', 20, 1) WITH LOG;
    RETURN;
END
GO

-- ============================================================
-- STEP 1: Rename WiFiSSID → WiFiBSSID on dbo.Offices
-- ============================================================
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'dbo'
      AND TABLE_NAME   = 'Offices'
      AND COLUMN_NAME  = 'WiFiSSID'
)
BEGIN
    EXEC sp_rename 'dbo.Offices.WiFiSSID', 'WiFiBSSID', 'COLUMN';
    PRINT 'OK — dbo.Offices.WiFiSSID renamed to WiFiBSSID';
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'dbo'
      AND TABLE_NAME   = 'Offices'
      AND COLUMN_NAME  = 'WiFiBSSID'
)
BEGIN
    PRINT 'SKIP — dbo.Offices.WiFiBSSID already exists (already migrated)';
END
ELSE
BEGIN
    PRINT 'WARNING — Column not found on dbo.Offices. Check table structure.';
END
GO

-- ============================================================
-- STEP 2: Rename WiFiSSID → WiFiBSSID on dbo.AttendanceLogs
-- (AttendanceLog.cs has WiFiSSID — logs the captured BSSID)
-- ============================================================
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'dbo'
      AND TABLE_NAME   = 'AttendanceLogs'
      AND COLUMN_NAME  = 'WiFiSSID'
)
BEGIN
    EXEC sp_rename 'dbo.AttendanceLogs.WiFiSSID', 'WiFiBSSID', 'COLUMN';
    PRINT 'OK — dbo.AttendanceLogs.WiFiSSID renamed to WiFiBSSID';
END
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'dbo'
      AND TABLE_NAME   = 'AttendanceLogs'
      AND COLUMN_NAME  = 'WiFiBSSID'
)
BEGIN
    PRINT 'SKIP — dbo.AttendanceLogs.WiFiBSSID already exists (already migrated)';
END
ELSE
BEGIN
    PRINT 'INFO — WiFiSSID column not found on dbo.AttendanceLogs (may not exist yet — OK)';
END
GO

-- ============================================================
-- VERIFICATION
-- ============================================================
PRINT '--- VERIFICATION ---';

SELECT
    TABLE_NAME   AS [Table],
    COLUMN_NAME  AS [Column],
    DATA_TYPE    AS [Type],
    CHARACTER_MAXIMUM_LENGTH AS [MaxLen]
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'dbo'
  AND COLUMN_NAME IN ('WiFiBSSID', 'WiFiSSID')
  AND TABLE_NAME IN ('Offices', 'AttendanceLogs')
ORDER BY TABLE_NAME, COLUMN_NAME;

-- Expected result:
--   Offices        | WiFiBSSID | nvarchar | 100
--   AttendanceLogs | WiFiBSSID | nvarchar | 200   (if column exists)
-- NO rows with WiFiSSID should appear.
GO

PRINT 'Migration complete. Bring app back online.';
GO