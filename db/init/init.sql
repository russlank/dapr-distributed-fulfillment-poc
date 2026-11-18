-- Creates the databases and one login per owning service (db_owner on its own database only).
-- Idempotent: rerunning updates passwords from .env. Passwords arrive as sqlcmd scripting
-- variables (environment variables of the db-init job). Tables come from each owning service.
SET NOCOUNT ON;
DECLARE @items TABLE (Db sysname, LoginName sysname, Pw nvarchar(128));
INSERT @items VALUES
    (N'OrderDb',              N'order_api',            N'$(ORDER_DB_PASSWORD)'),
    (N'InventoryDb',          N'inventory_service',    N'$(INVENTORY_DB_PASSWORD)'),
    (N'PaymentDb',            N'payment_service',      N'$(PAYMENT_DB_PASSWORD)'),
    (N'ShippingDb',           N'shipping_service',     N'$(SHIPPING_DB_PASSWORD)'),
    (N'CarrierSimDb',         N'carrier_simulator',    N'$(CARRIER_SIM_DB_PASSWORD)'),
    (N'PaymentProviderSimDb', N'payment_provider_sim', N'$(PAYMENT_PROVIDER_SIM_DB_PASSWORD)'),
    (N'DaprStateDb',          N'dapr_state',           N'$(DAPR_STATE_DB_PASSWORD)');

DECLARE @db sysname, @login sysname, @pw nvarchar(128), @sql nvarchar(max);
DECLARE items CURSOR LOCAL FAST_FORWARD FOR SELECT Db, LoginName, Pw FROM @items;
OPEN items;
FETCH NEXT FROM items INTO @db, @login, @pw;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF DB_ID(@db) IS NULL
    BEGIN
        SET @sql = N'CREATE DATABASE ' + QUOTENAME(@db) + N';';
        EXEC (@sql);
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET READ_COMMITTED_SNAPSHOT ON;';
        EXEC (@sql);
    END;

    IF SUSER_ID(@login) IS NULL
        SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@pw, '''')
                 + N', DEFAULT_DATABASE = ' + QUOTENAME(@db) + N', CHECK_POLICY = ON;';
    ELSE
        SET @sql = N'ALTER LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@pw, '''') + N';';
    EXEC (@sql);

    SET @sql = N'USE ' + QUOTENAME(@db) + N'; '
             + N'IF USER_ID(' + QUOTENAME(@login, '''') + N') IS NULL CREATE USER ' + QUOTENAME(@login)
             + N' FOR LOGIN ' + QUOTENAME(@login) + N'; '
             + N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@login) + N';';
    EXEC (@sql);
    PRINT N'ready: ' + @db + N' (login ' + @login + N')';
    FETCH NEXT FROM items INTO @db, @login, @pw;
END;
CLOSE items;
DEALLOCATE items;
