-- Corrective update of PLOSCA (unit_id = 2) stock_term rows to the agreed spec.
-- SR "Popravek - filtra v prikazu zalog": samot 80-220 belongs to SFM Keramika
-- with contains filter *plosca samot*; all other positions to be re-checked too.
--
-- Supersedes manual_stock_term_subunit_title_update.sql, whose UPDATEs keyed on the
-- OLD contains_text/exact_text values and therefore silently matched 0 rows wherever
-- that text had been entered incorrectly. This version keys on title, applies every
-- column, and REPORTS what it matched so a no-op can never pass unnoticed.
--
-- HOW TO RUN
--   1. @Apply = 0  -> preview only, nothing is written (default).
--   2. Read the "would change" result set.
--   3. @Apply = 1  -> applies inside a transaction.
--   4. Afterwards run verify_stock_terms_unit2.sql, then POST /api/stock/snapshots/refresh.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Apply bit = 0;   -- <== set to 1 to actually write

DECLARE @spec TABLE
(
    title         NVARCHAR(200) NOT NULL PRIMARY KEY,
    subunit_id    INT           NOT NULL,
    lgort         VARCHAR(4)    NOT NULL,
    contains_text NVARCHAR(200) NULL,
    exact_text    NVARCHAR(200) NULL
);

-- subunit_id: 5 = montaza, 6 = keramika, 13 = protektor
INSERT INTO @spec (title, subunit_id, lgort, contains_text, exact_text) VALUES
    (N'Sestavljanje sponk',            5,  '0012', N'sponka sestav',        NULL),
    (N'Izdelava VE',                   5,  '0012', N'element vezni',        NULL),
    (N'Izdelava VE VP',                5,  '0012', N'element vezni sestav', NULL),
    (N'Rezanje žice VP',               5,  '0012', N'žica rezana',          NULL),
    -- NOTE: casing matters. The SAP prefilter LIKEs the short text, and SAP LIKE is
    -- case sensitive, so the filter must be written exactly as MAKTX has it
    -- ("EGO Plošča šamot 180 2000/230"), not lower-cased.
    (N'šamot 80-220',                  6,  '0013', N'Plošča šamot',         NULL),   -- row flagged in the SR
    (N'Šamot VP',                      6,  '0013', N'EGO CHP',              NULL),
    (N'Spirale 80-220',                6,  '0013', NULL,                    N'spirala'),
    (N'Spirale VP',                    6,  '0013', N'SPIRALA CHP',          NULL),
    (N'Ulitek',                        6,  '0068', N'ulitek',               NULL),
    (N'E-protektor surovec',           13, '0016', NULL,                    N'protektor'),
    (N'Protektor sestav 80-220',       13, '0016', N'protektor sestav',     NULL),
    (N'Protektor sestav VP',           13, '0016', N'protektor CHP',        NULL),
    (N'NMK (71.006.030)',              13, '0016', NULL,                    N'nosilec mirovnega kontakta'),
    (N'NGK (71.001.007)',              13, '0016', NULL,                    N'nosilec gibljivega kontakta'),
    (N'Nosilec sestav (71.001.051)',   13, '0016', NULL,                    N'NOSILEC GIBLJIV.KONTAKTA-SESTA'),
    (N'Paličasti protektor surovec',   13, '0016', N'protektor paličasti',  NULL);

-- What the update would touch (rows that differ from the spec today).
SELECT
    'would change' AS action,
    t.term_id,
    s.title,
    t.subunit_id    AS from_subunit, s.subunit_id    AS to_subunit,
    t.lgort         AS from_lgort,   s.lgort         AS to_lgort,
    t.contains_text AS from_contains,s.contains_text AS to_contains,
    t.exact_text    AS from_exact,   s.exact_text    AS to_exact
FROM dbo.stock_term t
JOIN @spec s ON LTRIM(RTRIM(t.title)) = s.title
WHERE t.unit_id = 2
  AND (    ISNULL(t.subunit_id, -1)               <> s.subunit_id
        OR LTRIM(RTRIM(ISNULL(t.lgort, '')))      <> s.lgort
        OR LTRIM(RTRIM(ISNULL(t.contains_text, N''))) <> ISNULL(s.contains_text, N'')
        OR LTRIM(RTRIM(ISNULL(t.exact_text, N'')))    <> ISNULL(s.exact_text, N'') )
ORDER BY s.subunit_id, s.title;

-- Spec rows with no matching title in the DB: these need a manual decision
-- (the title itself was mistyped, or the row was never created).
SELECT 'no matching row - handle manually' AS action, s.*
FROM @spec s
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.stock_term t
    WHERE t.unit_id = 2 AND LTRIM(RTRIM(t.title)) = s.title);

IF @Apply = 1
BEGIN
    BEGIN TRANSACTION;

    UPDATE t
       SET t.subunit_id    = s.subunit_id,
           t.lgort         = s.lgort,
           t.contains_text = ISNULL(s.contains_text, N''),
           t.exact_text    = s.exact_text
    FROM dbo.stock_term t
    JOIN @spec s ON LTRIM(RTRIM(t.title)) = s.title
    WHERE t.unit_id = 2;

    PRINT 'rows updated: ' + CAST(@@ROWCOUNT AS VARCHAR(10));

    COMMIT TRANSACTION;
END
ELSE
    PRINT 'preview only - set @Apply = 1 to write';
