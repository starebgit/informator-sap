/*
    Stock goals for SAP stock terms.
    SQL Server script (SSMS) - run manually.
*/

IF OBJECT_ID('informator.dbo.stock_goal', 'U') IS NULL
BEGIN
    CREATE TABLE informator.dbo.stock_goal
    (
        id BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_stock_goal PRIMARY KEY,
        term_id INT NOT NULL,
        goal_value DECIMAL(18,3) NOT NULL,
        valid_from DATE NOT NULL,
        valid_to DATE NOT NULL,
        created_at DATETIME2(0) NOT NULL
            CONSTRAINT DF_stock_goal_created_at DEFAULT SYSUTCDATETIME(),
        updated_at DATETIME2(0) NOT NULL
            CONSTRAINT DF_stock_goal_updated_at DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_stock_goal_stock_term
            FOREIGN KEY (term_id) REFERENCES informator.dbo.stock_term(term_id),
        CONSTRAINT CK_stock_goal_valid_range
            CHECK (valid_from <= valid_to)
    );
END;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_stock_goal_term_period_latest'
      AND object_id = OBJECT_ID('informator.dbo.stock_goal')
)
BEGIN
    CREATE INDEX IX_stock_goal_term_period_latest
        ON informator.dbo.stock_goal (term_id, valid_from, valid_to, updated_at DESC, created_at DESC, id DESC)
        INCLUDE (goal_value);
END;
GO

IF OBJECT_ID('informator.dbo.TR_stock_goal_set_updated_at', 'TR') IS NULL
EXEC('
CREATE TRIGGER informator.dbo.TR_stock_goal_set_updated_at
ON informator.dbo.stock_goal
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE g
       SET updated_at = SYSUTCDATETIME()
    FROM informator.dbo.stock_goal g
    INNER JOIN inserted i
        ON i.id = g.id;
END;
');
GO
