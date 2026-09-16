-- Verification of PLOSCA (unit_id = 2) stock_term rows against the agreed spec
-- (source of truth = the "Ime enote / SFM / Skladiscna lokacija / Izraz / Filter" table
--  supplied with SR "Popravek - filtra v prikazu zalog").
-- READ ONLY. Run in SSMS against [informator]. Nothing is modified.
--
-- subunit_id: 5 = montaza, 6 = keramika, 13 = protektor

;WITH spec (title, subunit_id, lgort, contains_text, exact_text) AS
(
    SELECT * FROM (VALUES
        -- montaza (5), lgort 0012
        (N'Sestavljanje sponk',            5,  '0012', N'sponka sestav',        NULL),
        (N'Izdelava VE',                   5,  '0012', N'element vezni',        NULL),
        (N'Izdelava VE VP',                5,  '0012', N'element vezni sestav', NULL),
        (N'Rezanje žice VP',               5,  '0012', N'žica rezana',          NULL),
        -- keramika (6), lgort 0013 / 0068
        -- casing must match MAKTX exactly: SAP LIKE is case sensitive
        (N'šamot 80-220',                  6,  '0013', N'Plošča šamot',         NULL),   -- <== row flagged in the SR
        (N'Šamot VP',                      6,  '0013', N'EGO CHP',              NULL),
        (N'Spirale 80-220',                6,  '0013', NULL,                    N'spirala'),
        (N'Spirale VP',                    6,  '0013', N'SPIRALA CHP',          NULL),
        (N'Ulitek',                        6,  '0068', N'ulitek',               NULL),
        -- protektor (13), lgort 0016
        (N'E-protektor surovec',           13, '0016', NULL,                    N'protektor'),
        (N'Protektor sestav 80-220',       13, '0016', N'protektor sestav',     NULL),
        (N'Protektor sestav VP',           13, '0016', N'protektor CHP',        NULL),
        (N'NMK (71.006.030)',              13, '0016', NULL,                    N'nosilec mirovnega kontakta'),
        (N'NGK (71.001.007)',              13, '0016', NULL,                    N'nosilec gibljivega kontakta'),
        (N'Nosilec sestav (71.001.051)',   13, '0016', NULL,                    N'NOSILEC GIBLJIV.KONTAKTA-SESTA'),
        (N'Paličasti protektor surovec',   13, '0016', N'protektor paličasti',  NULL)
    ) AS v (title, subunit_id, lgort, contains_text, exact_text)
),
live AS
(
    SELECT
        term_id,
        unit_id,
        subunit_id,
        LTRIM(RTRIM(ISNULL(lgort, '')))                 AS lgort,
        LTRIM(RTRIM(ISNULL(contains_text, N'')))        AS contains_text,
        LTRIM(RTRIM(ISNULL(exact_text, N'')))           AS exact_text,
        LTRIM(RTRIM(ISNULL(title, N'')))                AS title,
        is_active
    FROM dbo.stock_term
    WHERE unit_id = 2
)

-- 1) Spec rows: matched by title, then every field compared.
SELECT
    CASE
        WHEN l.term_id IS NULL                                   THEN 'MISSING (no row with this title)'
        WHEN l.is_active = 0                                     THEN 'INACTIVE'
        WHEN ISNULL(l.subunit_id, -1) <> s.subunit_id            THEN 'WRONG SFM/subunit'
        WHEN l.lgort <> s.lgort                                  THEN 'WRONG lgort'
        WHEN l.contains_text <> ISNULL(s.contains_text, N'')     THEN 'WRONG contains filter'
        WHEN l.exact_text    <> ISNULL(s.exact_text, N'')        THEN 'WRONG exact filter'
        ELSE 'OK'
    END                                     AS status,
    s.title                                 AS spec_title,
    l.term_id,
    s.subunit_id    AS spec_subunit, l.subunit_id    AS db_subunit,
    s.lgort         AS spec_lgort,   l.lgort         AS db_lgort,
    s.contains_text AS spec_contains,l.contains_text AS db_contains,
    s.exact_text    AS spec_exact,   l.exact_text    AS db_exact,
    l.is_active
FROM spec s
LEFT JOIN live l
       ON l.title = s.title
ORDER BY
    CASE WHEN l.term_id IS NULL THEN 0 ELSE 1 END,
    s.subunit_id, s.title;

-- 2) Rows present in the DB for PLOSCA that the spec does not describe
--    (leftovers, duplicates, or rows whose title was mistyped).
SELECT 'EXTRA / UNKNOWN' AS status, l.*
FROM live l
WHERE NOT EXISTS (SELECT 1 FROM spec s WHERE s.title = l.title)
ORDER BY l.lgort, l.term_id;

-- 3) Raw dump of everything for PLOSCA, for eyeballing / pasting back.
SELECT term_id, unit_id, subunit_id, werks, lgort, contains_text, exact_text, title, is_active
FROM dbo.stock_term
WHERE unit_id = 2
ORDER BY lgort, term_id;
