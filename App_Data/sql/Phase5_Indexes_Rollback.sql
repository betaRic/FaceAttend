-- FaceAttend Phase 5 - rollback indexes

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_AttendanceLogs_EmployeeId_Timestamp'
      AND object_id = OBJECT_ID('dbo.AttendanceLogs')
)
BEGIN
    DROP INDEX IX_AttendanceLogs_EmployeeId_Timestamp ON dbo.AttendanceLogs;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_VisitorLogs_Timestamp'
      AND object_id = OBJECT_ID('dbo.VisitorLogs')
)
BEGIN
    DROP INDEX IX_VisitorLogs_Timestamp ON dbo.VisitorLogs;
END
GO
