-- Schematch demo fixture: TARGET database (drifted state that must be brought in line with the source)
-- Deliberate drift:
--   Customers: Email is nvarchar(200) (type), no UQ_Customers_Email, extra column LegacyCode, no CreatedAt default
--   Orders:    Total is decimal(10,2) (type), no CK_Orders_Total, no IX_Orders_CustomerId, Status default 'Pending'
--   Products:  missing entirely (only in source)
--   ObsoleteLog: exists only here (only in target)
--   vw_CustomerOrders: older definition; usp_GetCustomer: older body; fn_CustomerOrderCount + trigger: missing
--   Data: customer 5 missing, customer 6 extra, customer 3 has a different email
IF SCHEMA_ID(N'sales') IS NULL EXEC(N'CREATE SCHEMA [sales]');
GO

CREATE TABLE dbo.Customers (
    Id int IDENTITY(1,1) NOT NULL,
    Name nvarchar(100) NOT NULL,
    Email nvarchar(200) NULL,
    CreatedAt datetime2(7) NOT NULL,
    LegacyCode int NULL,
    CONSTRAINT PK_Customers PRIMARY KEY CLUSTERED (Id)
);
GO

CREATE TABLE sales.Orders (
    Id int IDENTITY(1,1) NOT NULL,
    CustomerId int NOT NULL,
    OrderDate datetime2(7) NOT NULL CONSTRAINT DF_Orders_OrderDate DEFAULT (sysutcdatetime()),
    Total decimal(10,2) NOT NULL,
    Status nvarchar(20) NOT NULL CONSTRAINT DF_Orders_Status DEFAULT (N'Pending'),
    LastModified datetime2(7) NULL,
    CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.ObsoleteLog (
    Id int IDENTITY(1,1) NOT NULL,
    Message nvarchar(400) NULL,
    CONSTRAINT PK_ObsoleteLog PRIMARY KEY CLUSTERED (Id)
);
GO

CREATE VIEW dbo.vw_CustomerOrders
AS
SELECT c.Id AS CustomerId, c.Name, o.Id AS OrderId, o.OrderDate, o.Total
FROM dbo.Customers c
JOIN sales.Orders o ON o.CustomerId = c.Id;
GO

CREATE PROCEDURE dbo.usp_GetCustomer @id int
AS
BEGIN
    SELECT Id, Name FROM dbo.Customers WHERE Id = @id;
END
GO

SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (Id, Name, Email, CreatedAt, LegacyCode) VALUES
    (1, N'Alice',  N'alice@example.com',      '2026-01-01T08:00:00', NULL),
    (2, N'Budi',   N'budi@example.com',       '2026-01-02T08:00:00', NULL),
    (3, N'Citra',  N'citra.old@example.com',  '2026-01-03T08:00:00', 7),
    (4, N'Dewi',   N'dewi@example.com',       '2026-01-04T08:00:00', NULL),
    (6, N'Fajar',  N'fajar@example.com',      '2026-01-06T08:00:00', NULL);
SET IDENTITY_INSERT dbo.Customers OFF;
GO

SET IDENTITY_INSERT sales.Orders ON;
INSERT INTO sales.Orders (Id, CustomerId, OrderDate, Total, Status, LastModified) VALUES
    (1, 1, '2026-02-01T10:00:00', 19.99, N'Shipped', NULL),
    (2, 2, '2026-02-02T11:00:00', 49.50, N'New', NULL);
SET IDENTITY_INSERT sales.Orders OFF;
GO
