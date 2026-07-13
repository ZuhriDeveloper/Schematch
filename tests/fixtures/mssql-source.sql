-- Schematch demo fixture: SOURCE database (the desired state)
IF SCHEMA_ID(N'sales') IS NULL EXEC(N'CREATE SCHEMA [sales]');
GO

CREATE TABLE dbo.Customers (
    Id int IDENTITY(1,1) NOT NULL,
    Name nvarchar(100) NOT NULL,
    Email nvarchar(255) NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_Customers PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UQ_Customers_Email UNIQUE (Email)
);
GO

CREATE TABLE dbo.Products (
    Id int IDENTITY(1,1) NOT NULL,
    Name nvarchar(200) NOT NULL,
    Sku nvarchar(50) NOT NULL,
    Price decimal(18,4) NOT NULL CONSTRAINT DF_Products_Price DEFAULT ((0)),
    CONSTRAINT PK_Products PRIMARY KEY CLUSTERED (Id)
);
GO
CREATE UNIQUE INDEX IX_Products_Sku ON dbo.Products (Sku);
GO

CREATE TABLE sales.Orders (
    Id int IDENTITY(1,1) NOT NULL,
    CustomerId int NOT NULL,
    OrderDate datetime2(7) NOT NULL CONSTRAINT DF_Orders_OrderDate DEFAULT (sysutcdatetime()),
    Total decimal(18,4) NOT NULL,
    Status nvarchar(20) NOT NULL CONSTRAINT DF_Orders_Status DEFAULT (N'New'),
    LastModified datetime2(7) NULL,
    CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id) ON DELETE CASCADE,
    CONSTRAINT CK_Orders_Total CHECK (Total > 0)
);
GO
CREATE INDEX IX_Orders_CustomerId ON sales.Orders (CustomerId);
GO

CREATE FUNCTION dbo.fn_CustomerOrderCount (@customerId int)
RETURNS int
AS
BEGIN
    RETURN (SELECT COUNT(*) FROM sales.Orders WHERE CustomerId = @customerId);
END
GO

CREATE VIEW dbo.vw_CustomerOrders
AS
SELECT c.Id AS CustomerId, c.Name, o.Id AS OrderId, o.OrderDate, o.Total, o.Status
FROM dbo.Customers c
JOIN sales.Orders o ON o.CustomerId = c.Id;
GO

CREATE PROCEDURE dbo.usp_GetCustomer @id int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Name, Email, CreatedAt FROM dbo.Customers WHERE Id = @id;
END
GO

CREATE TRIGGER trg_Orders_Touch ON sales.Orders
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE o SET LastModified = sysutcdatetime()
    FROM sales.Orders o
    JOIN inserted i ON i.Id = o.Id;
END
GO

SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (Id, Name, Email, CreatedAt) VALUES
    (1, N'Alice',  N'alice@example.com',  '2026-01-01T08:00:00'),
    (2, N'Budi',   N'budi@example.com',   '2026-01-02T08:00:00'),
    (3, N'Citra',  N'citra@example.com',  '2026-01-03T08:00:00'),
    (4, N'Dewi',   N'dewi@example.com',   '2026-01-04T08:00:00'),
    (5, N'Eko',    N'eko@example.com',    '2026-01-05T08:00:00');
SET IDENTITY_INSERT dbo.Customers OFF;
GO

SET IDENTITY_INSERT dbo.Products ON;
INSERT INTO dbo.Products (Id, Name, Sku, Price) VALUES
    (1, N'Widget', N'W-001', 19.9900),
    (2, N'Gadget', N'G-001', 49.5000);
SET IDENTITY_INSERT dbo.Products OFF;
GO

SET IDENTITY_INSERT sales.Orders ON;
INSERT INTO sales.Orders (Id, CustomerId, OrderDate, Total, Status, LastModified) VALUES
    (1, 1, '2026-02-01T10:00:00', 19.9900, N'Shipped', NULL),
    (2, 2, '2026-02-02T11:00:00', 49.5000, N'New', NULL),
    (3, 3, '2026-02-03T12:00:00', 69.4900, N'New', NULL);
SET IDENTITY_INSERT sales.Orders OFF;
GO
