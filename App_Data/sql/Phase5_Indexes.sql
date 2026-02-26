-- FaceAttend Phase 5 - recommended indexes
-- Target DB: SQL Server
-- Safe to run multiple times.

-- AttendanceLogs(EmployeeId, Timestamp)
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_AttendanceLogs_EmployeeId_Timestamp'
      AND object_id = OBJECT_ID('dbo.AttendanceLogs')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_AttendanceLogs_EmployeeId_Timestamp
    ON dbo.AttendanceLogs (EmployeeId, [Timestamp]);
END
GO

-- VisitorLogs(Timestamp)
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_VisitorLogs_Timestamp'
      AND object_id = OBJECT_ID('dbo.VisitorLogs')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_VisitorLogs_Timestamp
    ON dbo.VisitorLogs ([Timestamp]);
END
GO
