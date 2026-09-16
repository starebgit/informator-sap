# SR "Popravek filtra v prikazu zalog" — 16. 9. 2026

What was changed, and what to look at first if something looks wrong in **Zaloga**
(unit_id 2 = PLOŠČA; subunit 5 = montaža, 6 = keramika, 13 = protektor).

Two kinds of change: **data** (rows in `informator.dbo.stock_term` / `stock_goal` on
172.20.1.14) and **code** (`Services/StockSnapshotService.cs`). Data changes need no
deploy but only show up after the next snapshot refresh; the code change needed a
deploy but takes effect immediately.

## 1. Data changes — `dbo.stock_term`

| term_id | before | after | why |
|---|---|---|---|
| 4 | `Šamot`, contains `Šamot`, lgort 0013, subunit 5 (montaža), active | `is_active = 0` | Stale duplicate. It was the actual reported bug — šamot showing under montaža. The real row is term 17 under keramika. |
| 19 | `Spirale 80-220`, contains `spirala` | briefly `exact_text = 'spirala'`, then **reverted to contains `spirala`** | The exact-text attempt was a mistake and returned 0 (see below). |

```sql
UPDATE dbo.stock_term SET is_active = 0 WHERE term_id = 4;
UPDATE dbo.stock_term SET contains_text = N'spirala', exact_text = N'' WHERE term_id = 19;
```

### Why exact-text on term 19 failed
`exact` mode matches `MAKT-MAKTX` **in full**, not a substring. No material is named
just "spirala" — in plant 1061 / lgort 0013 there are 149 of them, all like
`SPIRALA 850W 230V 54,77 OHM`. So exact matched nothing and the card showed 0.
Verified directly against SAP (MARD + MAKT, 16. 9. 2026):

- non-CHP spirале: 149 materials, ~98 000 ST → this is `Spirale 80-220` (term 19)
- `SPIRALA CHP …`: 88 materials, 16 724 ST → this is `Spirale VP` (term 20)

**Known and accepted:** with `contains spirala`, term 19 also includes the CHP
materials, so those ~17 k ST are counted in both cards. This is long-standing
behaviour, not a regression. Fixing it would need a "must NOT contain" filter
fragment, which does not exist. **Not implemented — nobody asked for it.**

## 2. Data change — `dbo.stock_goal`

Goal id 9 (14 000, 2. 8. – 2. 10. 2026) moved from term 4 → **term 17**, so the
target follows `Šamot 80-220` now that term 4 is retired. The four expired goals
(Apr–Jul, ids 4/5/6/8) deliberately stayed on term 4 so past periods still report
the target that actually applied then.

## 3. Code change — deactivated terms no longer linger

Commit `497aa69`, merged as `f3a35cc`, published 16. 9. 2026 13:12.

**Symptom:** after term 4 was deactivated its card kept appearing under montaža,
showing a stale total (11 872) and yesterday's timestamp, while the page header
said "Zadnja posodobitev" of the newest run — so it looked current.

**Cause:** the two latest-per-term queries in `StockSnapshotService.cs` took
`ROW_NUMBER() … PARTITION BY term_id` over the whole `stock_summary_snapshot`
table — no time bound, no check against `stock_term`. Any term ever recorded kept
showing its last known row forever. The goal's expiry date would not have removed
the card either; it would only have dropped the `Cilj` value.

**Fix:** both CTEs now filter to `EXISTS (… stock_term t WHERE t.term_id = s.term_id
AND t.is_active = 1)`.

**Deliberately untouched:** the date-ranged history queries (`snapshots`,
`snapshots/by-date`). Looking at a past date should still show what was actually
measured then, retired terms included.

## 4. Where to look if something looks wrong

- **A card shows 0** → the term is probably in `exact` mode with a text that is not a
  complete MAKTX. Check `contains_text` / `exact_text` for that term_id.
- **A card shows stale numbers / a retired term reappears** → the `is_active` filter
  above; confirm the deployed build is newer than 16. 9. 2026 13:12.
- **A term edit had no effect** → term rows are only read during a snapshot refresh.
  `POST /api/stock/snapshots/refresh` is driven by **Task Scheduler on 172.20.1.14**,
  not from this workstation and not from inside the app. Wait for the next run.
- **Nothing changed after an UPDATE** → older scripts such as
  `sql/manual_stock_term_subunit_title_update.sql` key their UPDATEs on the *old*
  contains_text/exact_text, so they silently match 0 rows if that text was mistyped.
  Always key on `term_id`.
- **Casing:** SAP `LIKE` is case sensitive. `Plošča šamot` matches; `plošča šamot`
  returns 0. (`SapService.SearchMaterials` still has this bug plus a `ROWCOUNT 100`
  cap — unrelated to this SR, unfixed.)

## 5. Verified state after the 16. 9. 2026 11:42 run

19 cards, all populated, all from that run. Key values: Spirale 80-220 113 884 ST,
Spirale VP 16 724 ST, Šamot 80-220 10 207 ST (keramika), nothing under montaža.

## 6. Still open — needs the requester's call

- `Sponka` (term 5) and `Obroč` (term 6) are not in the SR spec. Left exactly as they
  were. (Term 6 produces no rows at all and never appears.)
- Terms 28/29/30 (`protektor 145/180/220`) still render as three cards all titled
  `Protektor sestav 80-220`; the spec wants a single row. Merging them into one
  `*protektor* *sestav*` term would also pull in `PROTEKTOR CHP SESTAV` (973 ST),
  which belongs to term 22 (VP) — so this needs the same "must NOT contain" filter
  that term 19 would need. **Explicitly deferred.**
