-- Drill-only control (S49, R02 variant C): puts the start request of ONE terminal order back to
-- Pending, simulating an operator error or a bug. Variables: $(OrderId), $(DueInSeconds) (the
-- request becomes due only then, so the drill can observe it Pending first), $(DrillRunId).
-- Refuses unless this is a drill database (the drill schema exists; only drill builds create it),
-- the order is terminal, its request is Closed, and it was not reset before without a restore.
-- The original row is kept in drill.StartRequestBackup; restore-start-request.sql puts it back.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF SCHEMA_ID('drill') IS NULL OR OBJECT_ID('drill.Failpoint') IS NULL
    THROW 50001, 'refused: not a drill database (no drill schema)', 1;
IF OBJECT_ID('drill.StartRequestBackup') IS NULL
    CREATE TABLE drill.StartRequestBackup (
        OrderId uniqueidentifier NOT NULL PRIMARY KEY, DrillRunId nvarchar(80) NOT NULL, State varchar(16) NOT NULL,
        StartedAt datetime2(3) NULL, ClosedAt datetime2(3) NULL, LastErrorCode varchar(64) NULL,
        NextAttemptAt datetime2(3) NOT NULL, ResetAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
DECLARE @o uniqueidentifier = '$(OrderId)';
IF NOT EXISTS (SELECT 1 FROM orders.[Order] WITH (UPDLOCK) WHERE OrderId = @o AND Status IN ('Completed', 'Rejected', 'Cancelled', 'Compensated', 'Escalated'))
    THROW 50002, 'refused: the order does not exist or is not terminal', 1;
IF NOT EXISTS (SELECT 1 FROM orders.WorkflowStartRequest WITH (UPDLOCK) WHERE OrderId = @o AND State = 'Closed')
    THROW 50003, 'refused: the start request is not Closed', 1;
IF EXISTS (SELECT 1 FROM drill.StartRequestBackup WHERE OrderId = @o)
    THROW 50004, 'refused: already reset (restore first)', 1;
INSERT drill.StartRequestBackup (OrderId, DrillRunId, State, StartedAt, ClosedAt, LastErrorCode, NextAttemptAt)
SELECT OrderId, '$(DrillRunId)', State, StartedAt, ClosedAt, LastErrorCode, NextAttemptAt FROM orders.WorkflowStartRequest WHERE OrderId = @o;
UPDATE orders.WorkflowStartRequest
SET State = 'Pending', StartedAt = NULL, ClosedAt = NULL, LastErrorCode = NULL,
    LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL,
    NextAttemptAt = DATEADD(second, $(DueInSeconds), SYSUTCDATETIME())
WHERE OrderId = @o;
IF @@ROWCOUNT <> 1
    THROW 50005, 'refused: expected exactly one start request', 1;
COMMIT;
SELECT 'reset';
