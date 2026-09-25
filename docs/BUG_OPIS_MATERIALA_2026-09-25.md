# Bug: "Opis materiala" empty on PLAN – Rezultati (2026-09-25)


## Report

User report: *"Oj a se je informator kaj spreminjali manjka opis materiala?"* ("Did something change on Informator? The material
description is missing.") Screenshot from 25.09, 11:16:
- Page: `http://172.20.1.40:3000/documentation/plan/results?plant=1061&term=2190V201` (Delovno mesto 2190V201, PAKIRANJE VELIKIH PLOŠČ).
- The **Opis materiala** column is empty on **every** row. Everything else is filled in (orders, dates, materials, quantities).
  **Dolgi tekst** shows for order 7086586 ("6.7.2026 Reklamacija v sk…"), so the long-text step works.
- Reported as "since yesterday" (24.09).

## ROOT CAUSE + FIX (2026-09-25, later the same day)

**Status: fixed on `fix/opis-materiala-where-split`, merged into `development`, published 25.09.2026.**

- **Cause:** the backend splits the RFC_READ_TABLE WHERE clause into 72-char OPTIONS lines at fixed positions, ignoring
  token boundaries. For 2190V201 the order list grew to **23 distinct materials** on 24.09. At 23 the cut falls inside `SPRAS`
  (`... ) AND SPR` | `AS = '5'`), SAP returns `OPTION_NOT_VALID`, and the `catch { return null; }` in `ReadTable` swallows it,
  so every material gets `""`. With 22 materials it works. A cut inside a quoted value is fine. A cut inside a keyword or
  field name is not.
- **Why "since yesterday":** the data changed (one more material in the list), not the code or SAP.
- **Fix:** new `InformatorSAP/Services/RfcWhere.cs` (`RfcWhere.Split`). It breaks lines only at spaces outside quotes, and the
  space starts the next line. It replaces all 6 copies of the hard split (5 in `SapService.cs`, `SapStockService.BuildWhereOptions`),
  since they all had the same latent bug. The new file is added to `InformatorSAP.csproj`.
- **Verified locally (IIS Express :58399):** 2190V201 went from 0/27 to 27/27 texts. Regression check on 2144V201: 31/31.
- **Diag:** replaying the backend's exact split with sapdiag reproduces it (23 materials → `OPTION_NOT_VALID`, 22 → OK).
- **Lesson:** always build RFC_READ_TABLE OPTIONS with `RfcWhere.Split`, never with fixed 72-char cuts. Also, `ReadTable`
  swallowing exceptions is why this showed up as blanks instead of an error.

The sections below are the investigation notes from before the cause was found.

## Findings so far

1. **Not caused by our code changes.**
   - Backend (`InformatorSAP`): the last commit on `development` is from **16.09** (`f97be75`). The Naročila commit `1392562` (24.09) is
     only on a local branch, was never pushed or deployed, and only adds new files (NarocilaBudget*, SapNarocilaService). It doesn't
     touch `SapService.cs`.
   - Last publish package on this machine: `obj/Release/Package/PackageTmp/bin/InformatorSAP.dll`, dated **16.09 13:49**.
     Nothing was deployed from here yesterday.
   - Frontend (`eta_informator`): the last commit is from **16.09** (`fdb26a5`).
   - The material text code (`SapService.GetOrdersByWorkCenter`, step "7) MAKT → material text",
     `InformatorSAP/Services/SapService.cs` ~line 1197) hasn't changed since **April 2026** (`4cb437f`).
2. **SAP has the texts.** A direct MAKT read via sapdiag (25.09) returns them. For example:
   - `000011448700020005` SPRAS `5` → "EGO Kom. Grelna plošča 400x400 5000/230"
   - `000011334542460005` → "EGO Kom. Grelna plošča 300x300 4000/230"
   - `000011300401540003` → "EGO Kom. Grelna plošča 300 4400/230"

   So the data is still in SAP. Something between SAP and the page is dropping it.
3. The MAKT step **swallows errors silently.** If `ReadTable("MAKT", …)` fails or returns nothing, every material gets `""`
   (`mtexts[m] = ""`) and the page just shows blanks. That fits "all rows empty, everything else fine".

## Suspects (not yet checked)

- **The MAKT RFC_READ_TABLE call now fails on the production server.** Possible causes: an SAP-side change (authorization for the RFC
  user on MAKT, an RFC_READ_TABLE change or note, or a length/option error on the `MATNR IN (…)` WHERE with 60 materials per chunk).
  Check the production backend's log for the `STEP("MAKT batch", "materials=N; texts=M")` line. `texts=N` but all empty means the read
  failed.
- **Language mapping:** `language=SL` → `spras`. It should be `'5'` (SL in MAKT is SPRAS `5`). Check how `spras` is derived in
  `GetOrdersByWorkCenter`. It hasn't changed in our code, but confirm.
- **A deploy or config change by someone else on the server** (not from this machine). Ask Blaž Klasič whether anything was published
  or changed on 24.09. Check the file dates of the deployed `InformatorSAP.dll` on the production server.
- Does any other page that shows MAKT texts (kosovnica, stock) also show blanks now? That would point to SAP/RFC, not the plan page.

## Next steps

1. Run the backend locally (VS F5, or IIS Express `/port:58399`, see `docs/narocila-budget/PROGRESS.md`) and call
   `GET /api/plan/orders-by-workcenter?plant=1061&workCenter=2190V201&language=SL&take=500`. Does `MaterialText` (or whatever the
   field is called) come back filled? Check the debug output for the `[GetOrdersByWorkCenter] MAKT batch` step.
   - **Filled locally** → the problem is on the production server (its deploy, config or RFC user). Check the server.
   - **Empty locally too** → an SAP-side change. Catch and log the MAKT `ReadTable` exception to see the RFC error.
2. I couldn't reach the production API from here (`https://172.20.1.14:5025` → TLS "connection closed"). That port may not be the
   production backend. Find the real one in the production frontend's `.env` (`REACT_APP_INFORMATORSAP`).
3. Fix, then **commit/deploy only when Beno says so.**

## Tool notes

- sapdiag (built copy): `%TEMP%\claude\C--Users-stareb-source-repos-InformatorSAP\061b268b-d8e0-4c5e-bd3b-6819d2e0d118\scratchpad\sapdiag\out\sapdiag.exe`.
  It needed a rebuild (`dotnet build -c Release -o out`) because `System.Configuration.ConfigurationManager.dll` was missing. Source is in
  `docs/narocila-budget/tools/sapdiag/`.
- The RFC_READ_TABLE WHERE clause needs spaces inside the parentheses: `MATNR IN ( 'a','b' )`. `IN ('a','b')` gives `OPTION_NOT_VALID`.
  Example: `sapdiag.exe table MAKT MATNR,SPRAS,MAKTX "MATNR = '000011448700020005'" 20`
