-- FaceAttend — WiFiBSSID Column Verification
-- Plan 8 Phase G
-- Idempotent: safe to run multiple times

PRINT 'Checking WiFiBSSID column names...';

-- Check Offices table
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Offices' AND COLUMN_NAME = 'WiFiBSSID')
    PRINT 'Offices.WiFiBSSID — OK';
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'Offices' AND COLUMN_NAME = 'WifiBSSID')
BEGIN
    PRINT 'Offices.WifiBSSID needs rename — applying...';
    EXEC sp_rename 'dbo.Offices.WifiBSSID', 'WiFiBSSID', 'COLUMN';
    PRINT 'Done.';
END
ELSE
    PRINT 'WARNING: Offices table has no WiFiBSSID column — check schema.';

-- Check AttendanceLogs table
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'AttendanceLogs' AND COLUMN_NAME = 'WiFiBSSID')
    PRINT 'AttendanceLogs.WiFiBSSID — OK';
ELSE IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'AttendanceLogs' AND COLUMN_NAME = 'WifiBSSID')
BEGIN
    PRINT 'AttendanceLogs.WifiBSSID needs rename — applying...';
    EXEC sp_rename 'dbo.AttendanceLogs.WifiBSSID', 'WiFiBSSID', 'COLUMN';
    PRINT 'Done.';
END
ELSE
    PRINT 'WARNING: AttendanceLogs has no WiFiBSSID column — check schema.';

PRINT 'WiFiBSSID verification complete.';
