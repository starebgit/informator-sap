-- Manual migration for SQL Server (run in SSMS)
-- Adds support for exact-text SAP stock search configuration and snapshot logging.

BEGIN TRANSACTION;

-- 1) Search configuration table: add optional exact text parameter.
IF COL_LENGTH('informator.dbo.stock_term', 'exact_text') IS NULL
BEGIN
    ALTER TABLE informator.dbo.stock_term
    ADD exact_text NVARCHAR(200) NULL;
END;

-- 2) Snapshot table: log exact text and chosen search mode.
IF COL_LENGTH('informator.dbo.stock_summary_snapshot', 'exact_text') IS NULL
BEGIN
    ALTER TABLE informator.dbo.stock_summary_snapshot
    ADD exact_text NVARCHAR(200) NULL;
END;

IF COL_LENGTH('informator.dbo.stock_summary_snapshot', 'search_mode') IS NULL
BEGIN
    ALTER TABLE informator.dbo.stock_summary_snapshot
    ADD search_mode NVARCHAR(20) NOT NULL
        CONSTRAINT DF_stock_summary_snapshot_search_mode DEFAULT ('contains');
END;

-- Backfill mode for existing rows (if exact_text was ever populated manually).
UPDATE informator.dbo.stock_summary_snapshot
SET search_mode = CASE
    WHEN exact_text IS NOT NULL AND LTRIM(RTRIM(exact_text)) <> '' THEN 'exact'
    ELSE 'contains'
END
WHERE search_mode IS NULL
   OR search_mode NOT IN ('contains', 'exact');

COMMIT TRANSACTION;
