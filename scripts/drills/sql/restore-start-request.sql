-- Drill-only control: undoes reset-start-request.sql for $(OrderId): the start request gets its
-- saved state, timestamps, and error code back (whatever happened to it meanwhile), and the backup
-- row is removed. Refuses when there is nothing to restore.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @o uniqueidentifier = '$(OrderId)';
IF OBJECT_ID('drill.StartRequestBackup') IS NULL OR NOT EXISTS (SELECT 1 FROM drill.StartRequestBackup WHERE OrderId = @o)
    THROW 50011, 'refused: no saved start request for this order', 1;
DECLARE @before varchar(80) = (SELECT CONCAT(State, '/', ISNULL(LastErrorCode, '-')) FROM orders.WorkflowStartRequest WHERE OrderId = @o);
UPDATE r
SET State = b.State, StartedAt = b.StartedAt, ClosedAt = b.ClosedAt, LastErrorCode = b.LastErrorCode, NextAttemptAt = b.NextAttemptAt,
    LeaseOwner = NULL, LeaseExpiresAt = NULL, LeaseToken = NULL
FROM orders.WorkflowStartRequest r JOIN drill.StartRequestBackup b ON b.OrderId = r.OrderId
WHERE r.OrderId = @o;
DELETE drill.StartRequestBackup WHERE OrderId = @o;
COMMIT;
SELECT CONCAT('restored (was ', @before, ')');
