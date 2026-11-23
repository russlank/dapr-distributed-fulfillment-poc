-- Drill-only control (S15, R08 variant C): creates an orphan hold, as a fulfillment run leaves
-- behind when it dies after its reservation committed and never comes back: the units are reserved
-- (Reserved += q), the hold is Held with an expiry in $(ExpiresInSeconds) seconds by SQL time, and
-- its HoldCreated outbox row exists, exactly as the guarded reservation writes them. No workflow
-- owns it: only the sweeper can take it out of Held. Variables: $(OrderId), $(WarehouseId), $(Sku),
-- $(Quantity), $(ExpiresInSeconds).
-- Refuses unless this is a drill database (the drill schema exists; only drill builds create it)
-- and the stock row can hold the quantity.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF SCHEMA_ID('drill') IS NULL OR OBJECT_ID('drill.Failpoint') IS NULL
    THROW 50001, 'refused: not a drill database (no drill schema)', 1;
DECLARE @o uniqueidentifier = '$(OrderId)', @w varchar(16) = '$(WarehouseId)', @s varchar(32) = '$(Sku)', @q int = $(Quantity);
UPDATE inventory.StockItem SET Reserved = Reserved + @q WHERE WarehouseId = @w AND Sku = @s AND OnHand - Reserved >= @q;
IF @@ROWCOUNT <> 1
    THROW 50002, 'refused: no stock row with enough free units', 1;
DECLARE @hold TABLE (HoldId uniqueidentifier);
INSERT inventory.Hold (OrderId, LineNumber, WarehouseId, Sku, Quantity, State, ExpiresAt, CreatedBy)
OUTPUT inserted.HoldId INTO @hold
VALUES (@o, 1, @w, @s, @q, 'Held', DATEADD(second, $(ExpiresInSeconds), SYSUTCDATETIME()), 'drill-orphan');
-- The payload has the fields the guarded reservation writes, so the event is valid for its consumer.
INSERT inventory.Outbox (OrderId, Sequence, EventType, HoldId, Payload)
SELECT @o, 1, 'HoldCreated', h.HoldId,
       CONCAT('{"holdId":"', LOWER(CONVERT(varchar(36), h.HoldId)), '","orderId":"', LOWER(CONVERT(varchar(36), @o)), '","lineNumber":1,"warehouseId":"', @w,
              '","sku":"', @s, '","quantity":', @q, ',"expiresAt":"', CONVERT(varchar(23), x.ExpiresAt, 126), 'Z"}')
FROM @hold h JOIN inventory.Hold x ON x.HoldId = h.HoldId;
COMMIT;
SELECT LOWER(CONVERT(varchar(36), HoldId)) FROM @hold;
