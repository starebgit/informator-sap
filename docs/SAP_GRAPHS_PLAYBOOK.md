# SAP Graphs Playbook (Izmet / SO style)

How to add a new "SAP graph" end‑to‑end from an Excel export. Written so a fresh
session can do the whole thing from: *"here's the Excel, make a graph on subunit
`___`, name it `___`."*

This file is duplicated in both repos (kept identical):
- Backend: `informator-sap` → `docs/SAP_GRAPHS_PLAYBOOK.md`
- Frontend: `eta_informator` → `docs/SAP_GRAPHS_PLAYBOOK.md`

Existing implementations to copy from:
- Backend: `Services/SapMaterialDocService.cs` (`GetIzmetRatio`, `GetTermostatIzmetRatio`),
  `Controllers/MaterialDocsController.cs`, `Services/SapGraphGoalService.cs`.
- Frontend: `src/components/IzmetTermostatGraph/`, `src/components/IzmetSapGraph/`,
  `src/components/SapGraph/` (`useGraphGoal.js`, `SapGraphControls.js`),
  `src/containers/Shopfloor/Quality/Quality.js` (injection point).

---

## 0. What these graphs are

Every one of these graphs is a replication of a **ZPP_0117 / MB51 goods‑movement
report** filtered + aggregated in C# (dialog transactions can't be called; we read
tables via `RFC_READ_TABLE` on NCo). The Excel the user hands over **is** that
report exported, plus one or two pivot tables that define the filtering/aggregation.

The backend never returns the whole report to the browser — it returns a small
per‑day aggregate (`IzmetRatioResult`). The graph plots it; a goal line + Excel
export sit on top.

---

## 1. Decode the Excel (do this first, offline)

The `.xlsx` is a zip. Unzip it and read the XML. There is **no Python** on this box;
use `unzip` + `perl`/`node`. Key parts:

| Part | What it gives you |
|---|---|
| `xl/sharedStrings.xml` | all text values (indexed) |
| `xl/worksheets/sheetN.xml` | cell values; map sheetN→name via `xl/workbook.xml` + `_rels` |
| `xl/pivotCache/pivotCacheDefinition1.xml` | each cache field's shared items (the filter *values*) |
| `xl/pivotTables/pivotTableN.xml` | which fields are page/row/data; which items are **visible** vs hidden (`h="1"`) |
| `xl/media/image1.png` | screenshot of the **ZPP_0117 selection screen** = the exact input params |
| a **"Data"** sheet | the raw MB51 export (already signed); your ground truth |
| a **"Kriteriji"** sheet | usually empty cells — its drawing is the selection‑screen screenshot |

### 1a. The 19 MB51 columns (cache field order == Data column order)

```
0 Mesec              (SPMON)          10 Znes.v dom.val.   (DMBTR, €)
1 Datum knjiženja    (BUDAT_MKPF)     11 Valuta            (WAERS)
2 Material           (MATNR)          12 Post.dok.materiala
3 Kratki tks. mat.   (MAKTX)          13 Dobavitelj        (LIFNR)
4 Razred vrednotenja (BKLAS, MBEW)    14 Leto dok. mat.    (MJAHR)
5 Vrsta premika      (BWART)          15 Dokument mat.     (MBLNR)
6 Skladiščna lok.    (LGORT)          16 Naročilo          (EBELN)
7 Vrsta vrednotenja  (BWTAR)          17 Profitni center   (PRCTR, MARC)
8 Količina           (MENGE)          18 Nalog             (AUFNR)
9 Osnovna mer. enota (MEINS)
```

### 1b. Read the pivot(s) to get the recipe
- **Page fields** (`axisPage`) = fixed filters. Visible item(s) = the value(s) kept
  (e.g. BWART `551`, BKLAS `{3100,4100}`).
- **Row field** (`axisRow`) = grouping; if most items are `h="1"` the user **hand‑picked**
  a material subset — you must reproduce that filter or the numbers won't match.
  Match on the (ASCII‑safe part of the) short text, e.g. `MAKTX == "EGO TERMOSTAT"
  || MAKTX.StartsWith("PODNO")`.
- **Data fields** = the measures (sum of `Količina`, sum of `Znes.v dom.val.`).
- A workbook often has **two** pivots: one for **Izmet** (scrap, e.g. BWART 551) and
  one for **SO / Donos** (shop output, BWART 101/102, BKLAS 4100). The ratio graph =
  Izmet€ ÷ SO€.

### 1c. The Kriteriji screenshot = the real input scope
Read `xl/media/image1.png`. It shows werks / bukrs / mjahr / **Datum knjiženja range** /
**Profitni center**. The profit center often is **not** a pivot page field but still
scopes the whole export — don't miss it.

### 1d. Validate offline BEFORE writing code
The Data sheet already has BKLAS/BWART/MAKTX/qty/€ and is already sign‑adjusted.
Sum your intended classification over the Data rows and confirm it equals the pivot
grand total. This catches the recipe being wrong before any SAP/C# work.
(Watch out: matching non‑ASCII short texts like `PODNOŽJE` in a quick perl script can
silently fail on encoding — match on an ASCII prefix.)

Also: "two Excel files" are often byte‑identical duplicates (a re‑download with a
timestamp suffix). Diff them before assuming they differ.

---

## 2. Backend (`informator-sap`)

Mirror `GetTermostatIzmetRatio` in `Services/SapMaterialDocService.cs`.

1. **Constants** for the classification (BWART, BKLAS set, material predicate).
2. **Read MSEG** with `ReadTable("MSEG", fields, BuildWhereOptions(where), MaxSapRows)`.
   WHERE = `WERKS`, `MJAHR`, `BUKRS`, `BWART IN (...)` (union of both pivots), and the
   `BUDAT_MKPF` range. Push only what MSEG can filter; BKLAS/PRCTR are applied after.
3. **Batched lookups** (INNER‑JOIN semantics — drop rows missing any):
   `LookupMbew` (BKLAS), `LookupMarc` (PRCTR), `LookupMara` (existence),
   `LookupMakt` (MAKTX for the material filter), `LookupUnits` if needed.
4. **Classify + sum**: sign by `SHKZG == "H" ? -1 : 1`; `ParseScaled(dmbtr, 2)`; bucket
   per `BUDAT_MKPF` day into SO vs Izmet; also keep running totals.
5. Return `IzmetRatioResult` (`Days[]` of `{PostingDate, ShopOutput, Izmet, Ratio,
   RatioPercent}` + period totals). Reuse the existing DTOs.
6. **Controller**: add a `[Route("...")]` in `Controllers/MaterialDocsController.cs`
   taking `mjahr` (required), `werks`(1061), `bukrs`(1060), `budatFrom/To`, `prctr`, `lang`.
7. **⚠ Non‑SDK csproj**: add every new `.cs` to `InformatorSAP.csproj` as
   `<Compile Include="..." />` or it's silently not compiled.

**Gotchas**
- `RFC_READ_TABLE` OPTIONS are concatenated with a space every 72 chars → never split a
  token; build `IN( 'a', 'b' )` space‑separated. `BuildWhereOptions` already handles it.
- Don't put non‑ASCII string literals in C# source (codepage risk) — match short texts
  on ASCII substrings.

**Test (must match the Excel exactly):**
```bash
# start the app
iisexpress /config:".vs/InformatorSAP/config/applicationhost.config" /site:InformatorSAP
# call with the Kriteriji params
curl "http://localhost:58396/api/material-docs/<route>?mjahr=2026&budatFrom=2026-06-21&budatTo=2026-06-28&prctr=11032005"
```
Confirm the totals equal the pivot grand total (e.g. Izmet −1192.73, SO 183733.26).

**Goals** are generic — no per‑graph backend work. `/api/graph-goals`
(`SapGraphGoalService`, table `informator.dbo.sap_graph_goal`, auto‑created) stores one
value per `graphKey`. Just pick a `graphKey` string for the new graph.

---

## 3. Frontend (`eta_informator`)

1. **Find the subunit keyword.** Search `src/i18n/sl.json` for the label the user gave
   (e.g. "Montaža 55.17" → key `termo_55`) and confirm it's in
   `src/utils/utils.js` `subunitToUnitMap`. That keyword is what `selectedUnit?.keyword`
   equals in the container.
2. **Injection point** = `src/containers/Shopfloor/Quality/Quality.js`. Graphs are
   injected as the first card(s) of the machine‑group mosaic, keyed on
   `selectedUnit?.keyword`. Extend `qualityGridChildren` (prepend `<GridItem>`s) and
   `qualityLayouts` (the uniform 6/12 cascade). Two side‑by‑side graphs = slots x:0 and x:6.
3. **Graph component.** Copy `IzmetTermostatGraph.js`:
   - fetch `${REACT_APP_INFORMATORSAP}/api/material-docs/<route>?...&prctr=<pc>` via
     react‑query; `Bar` from `react-chartjs-2` + `chart.js/auto`.
   - register `chartjs-plugin-annotation`; add the goal line via `goalAnnotation(...)`.
   - render `<SapGraphControls>` in the card header.
   - one component can serve two graphs via a `mode` prop (ratio vs value).
4. **Goal + export** are shared:
   - `useGraphGoal(graphKey)` → `{ goalValue, saveGoal, isSaving }` (+ `goalAnnotation`,
     `GOAL_EDITOR_USERNAMES`) in `src/components/SapGraph/useGraphGoal.js`.
   - `<SapGraphControls>` renders the **"Cilj"** button (only for
     `GOAL_EDITOR_USERNAMES` = `["zorjanr","stareb","klasicb"]`) and the **Excel** button.
   - Export = **graph values only** (date + plotted value) for a from/to range the user
     picks; the graph supplies `fetchExportRows(from, to)` which re‑queries the same
     ratio endpoint and maps `Days` → `{ Datum, <title> }`. No raw‑row endpoint.
5. **i18n**: add the title key(s) to `sl/en/de.json` (shopfloor namespace, near
   `izmet_so_ratio`). The graph title carries its unit (`%` / `€`).
6. **prctr per graph** is hard‑coded in the fetch (e.g. `11032001` plošče, `11032005`
   termostat) — read it from the Kriteriji screenshot.

**Verify**: `npx eslint <files>`, `node -e JSON.parse` the locales, `CI=false npx craco build`.

---

## 4. Git / deploy flow

- **Frontend** (`eta_informator`): branch off `production`, commit, push branch,
  `git merge --no-ff` into `production`, push `production`.
- **Backend** (`informator-sap`): commit on `feature/material-docs`, `git merge --no-ff`
  into `development`, push. **Remote quirk:** `origin` *fetches from GitHub* but its
  *push URL is the Bitbucket mirror*; `development`/feature branches track `github/*`, so
  `git push` (upstream) goes to GitHub. Ask before pushing to Bitbucket.
- The backend must be **deployed** for the new endpoint (and `/api/graph-goals`) to exist.

---

## 5. Quick checklist for a new graph

- [ ] Unzip Excel; read pivot(s) + Kriteriji screenshot; write down BWART/BKLAS/material
      filter, measures, and werks/bukrs/mjahr/budat/prctr.
- [ ] Validate the recipe against the Data sheet (totals match the pivot).
- [ ] Backend: new service method + controller route; add files to `.csproj`;
      run IIS Express and confirm totals match the Excel.
- [ ] Frontend: find subunit keyword; inject in `Quality.js`; graph component;
      i18n titles; goal line + `SapGraphControls`.
- [ ] eslint + JSON + `craco build`.
- [ ] Merge/push both repos; remind to deploy the backend.
