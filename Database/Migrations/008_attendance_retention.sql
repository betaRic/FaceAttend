-- FaceAttend — Attendance Log Retention
-- Plan 8 Phase H
-- Run this to create the retention procedure.
-- Execution is manual — call from SSMS or a scheduled SQL Agent job.
-- SQL Express note: SQL Agent is not available on Express; run manually as needed.

IF OBJECT_ID('dbo.sp_PurgeOldAttendanceLogs', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_PurgeOldAttendanceLogs;
GO

CREATE PROCEDURE dbo.sp_PurgeOldAttendanceLogs
    @RetentionYears INT = 5,
    @BatchSize      INT = 500,
    @DryRun         BIT = 1   -- 1 = count only, 0 = actually delete
AS
BEGIN
    SET NOCOUNT ON;

    IF @RetentionYears < 1 SET @RetentionYears = 1;
    IF @BatchSize < 100   SET @BatchSize = 100;
    IF @BatchSize > 5000  SET @BatchSize = 5000;

    DECLARE @Cutoff  DATETIME = DATEADD(YEAR, -@RetentionYears, GETUTCDATE());
    DECLARE @Deleted INT      = 0;
    DECLARE @Total   INT;

    SELECT @Total = COUNT(*)
    FROM dbo.AttendanceLogs
    WHERE Timestamp < @Cutoff;

    PRINT 'Cutoff: ' + CONVERT(VARCHAR, @Cutoff, 120);
    PRINT 'Rows older than cutoff: ' + CAST(@Total AS VARCHAR);

    IF @DryRun = 1
    BEGIN
        PRINT 'DryRun=1 — no rows deleted. Set @DryRun=0 to purge.';
        RETURN;
    END

    WHILE 1 = 1
    BEGIN
        DELETE TOP (@BatchSize)
        FROM dbo.AttendanceLogs
        WHERE Timestamp < @Cutoff;

        SET @Deleted = @Deleted + @@ROWCOUNT;
        IF @@ROWCOUNT = 0 BREAK;
    END

    PRINT 'Deleted: ' + CAST(@Deleted AS VARCHAR) + ' rows.';
END
GO

-- Usage examples:
--   Preview only (no deletion):
--     EXEC dbo.sp_PurgeOldAttendanceLogs @RetentionYears = 5, @DryRun = 1;
--
--   Actually purge records older than 5 years:
--     EXEC dbo.sp_PurgeOldAttendanceLogs @RetentionYears = 5, @DryRun = 0;
