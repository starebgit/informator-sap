-- SIMPLE SQL (run in SSMS)
-- Adds two columns to stock_term and fills subunit_id + title values.

-- 1) Add columns if missing
IF COL_LENGTH('informator.dbo.stock_term', 'subunit_id') IS NULL
BEGIN
    ALTER TABLE informator.dbo.stock_term ADD subunit_id INT NULL;
END;

IF COL_LENGTH('informator.dbo.stock_term', 'title') IS NULL
BEGIN
    ALTER TABLE informator.dbo.stock_term ADD title NVARCHAR(200) NULL;
END;

-- IMPORTANT:
-- SQL Server validates column names at parse time for the whole batch.
-- Use GO so UPDATE/SELECT statements are compiled only after ALTER TABLE.
GO

-- 2) Fill data (PLOŠČA = unit_id 2)
-- Montaža (subunit_id = 5)
UPDATE informator.dbo.stock_term SET subunit_id = 5, title = N'Sestavljanje sponk'
WHERE unit_id = 2 AND lgort = '0012' AND contains_text = N'sponka sestav';

UPDATE informator.dbo.stock_term SET subunit_id = 5, title = N'Izdelava VE'
WHERE unit_id = 2 AND lgort = '0012' AND contains_text = N'element vezni' AND ISNULL(exact_text, N'') = N'';

UPDATE informator.dbo.stock_term SET subunit_id = 5, title = N'Izdelava VE VP'
WHERE unit_id = 2 AND lgort = '0012' AND contains_text = N'element vezni sestav';

UPDATE informator.dbo.stock_term SET subunit_id = 5, title = N'Rezanje žice VP'
WHERE unit_id = 2 AND lgort = '0012' AND contains_text = N'žica rezana';

-- Protektor (subunit_id = 13)
UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'E-protektor surovec'
WHERE unit_id = 2 AND lgort = '0016' AND ISNULL(exact_text, N'') = N'protektor';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'Protektor sestav 80-220'
WHERE unit_id = 2 AND lgort = '0016' AND contains_text = N'protektor sestav';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'Protektor sestav VP'
WHERE unit_id = 2 AND lgort = '0016' AND contains_text = N'protektor CHP';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'NMK (71.006.030)'
WHERE unit_id = 2 AND lgort = '0016' AND ISNULL(exact_text, N'') = N'nosilec mirovnega kontakta';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'NGK (71.001.007)'
WHERE unit_id = 2 AND lgort = '0016' AND ISNULL(exact_text, N'') = N'nosilec gibljivega kontakta';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'Nosilec sestav (71.001.051)'
WHERE unit_id = 2 AND lgort = '0016' AND ISNULL(exact_text, N'') = N'NOSILEC GIBLJIV.KONTAKTA-SESTA';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'Paličasti protektor surovec'
WHERE unit_id = 2 AND lgort = '0016' AND contains_text = N'protektor paličasti';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'Protektor sestav 80-220'
WHERE unit_id = 2 AND lgort = '0016' AND contains_text = N'protektor 145';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'Protektor sestav 80-220'
WHERE unit_id = 2 AND lgort = '0016' AND contains_text = N'protektor 180';

UPDATE informator.dbo.stock_term SET subunit_id = 13, title = N'Protektor sestav 80-220'
WHERE unit_id = 2 AND lgort = '0016' AND contains_text = N'protektor 220';

-- Keramika (subunit_id = 6)
UPDATE informator.dbo.stock_term SET subunit_id = 6, title = N'šamot 80-220'
WHERE unit_id = 2 AND lgort = '0013' AND contains_text = N'plošča šamot';

UPDATE informator.dbo.stock_term SET subunit_id = 6, title = N'Šamot VP'
WHERE unit_id = 2 AND lgort = '0013' AND contains_text = N'EGO CHP';

UPDATE informator.dbo.stock_term SET subunit_id = 6, title = N'Spirale 80-220'
WHERE unit_id = 2 AND lgort = '0013' AND ISNULL(exact_text, N'') = N'spirala';

UPDATE informator.dbo.stock_term SET subunit_id = 6, title = N'Spirale VP'
WHERE unit_id = 2 AND lgort = '0013' AND contains_text = N'SPIRALA CHP';

UPDATE informator.dbo.stock_term SET subunit_id = 6, title = N'Ulitek'
WHERE unit_id = 2 AND lgort = '0068' AND contains_text = N'ulitek';

-- Optional check
SELECT term_id, contains_text, exact_text, lgort, unit_id, subunit_id, title
FROM informator.dbo.stock_term
WHERE unit_id = 2
ORDER BY lgort, term_id;
