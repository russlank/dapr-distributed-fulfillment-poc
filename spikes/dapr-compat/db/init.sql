IF DB_ID('DaprStateDb') IS NULL CREATE DATABASE DaprStateDb;
IF DB_ID('SpikeDb') IS NULL CREATE DATABASE SpikeDb;
GO
USE SpikeDb;
IF OBJECT_ID('dbo.Evidence') IS NULL
    CREATE TABLE dbo.Evidence (
        Id     bigint IDENTITY PRIMARY KEY,
        At     datetime2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        Kind   nvarchar(40)  NOT NULL,
        [Key]  nvarchar(200) NOT NULL,
        Host   nvarchar(40)  NOT NULL,
        Detail nvarchar(400) NOT NULL
    );
GO
