using System.Collections.Generic;

namespace InformatorSAP.Classes
{
    /// <summary>
    /// One detail row of the ZPP_0117 / MB51-style goods-movement report
    /// (so_xchpf initial = "Pogled detajlov").
    /// </summary>
    public class MaterialDocRowDto
    {
        public string Period { get; set; }            // Mesec     SPMON (yyyymm)
        public string PostingDate { get; set; }       // Dat.knj   MSEG-BUDAT_MKPF (yyyy-MM-dd)
        public string Material { get; set; }          // Material  MSEG-MATNR (leading zeros stripped)
        public string MaterialText { get; set; }      // Kratko besedilo materiala  MAKT-MAKTX
        public string ValuationClass { get; set; }    // Razred    MBEW-BKLAS
        public string MovementType { get; set; }      // VrP       MSEG-BWART
        public string StorageLocation { get; set; }   // SLok      MSEG-LGORT
        public string ValuationType { get; set; }     // Vrs.vred  MSEG-BWTAR
        public decimal Quantity { get; set; }         // Količina  MSEG-MENGE (signed by SHKZG)
        public string Unit { get; set; }              // EME       external unit (T006B-MSEH3)
        public decimal Amount { get; set; }           // Vrednost  MSEG-DMBTR (signed by SHKZG)
        public string Currency { get; set; }          // Valuta    MSEG-WAERS
        public string Item { get; set; }              // Pos       MSEG-ZEILE
        public string Vendor { get; set; }            // Dobavitelj MSEG-LIFNR
        public string DocYear { get; set; }           // MLeto     MSEG-MJAHR
        public string MaterialDoc { get; set; }       // Dok.mat   MSEG-MBLNR
        public string PurchaseOrder { get; set; }     // Naročilo  MSEG-EBELN
        public string ProfitCenter { get; set; }      // Profitni cent  MARC-PRCTR
        public string SpecialProcurement { get; set; }// Posebna nabava MARC-SOBSL
    }

    /// <summary>
    /// One aggregated row of the summary view (so_xchpf = 'X' = "Sumarni pogled").
    /// Mirrors the ABAP COLLECT into it_mseg_alv: MENGE + DMBTR summed by the
    /// non-numeric key fields below.
    /// </summary>
    public class MaterialDocSummaryRowDto
    {
        public string Plant { get; set; }             // MSEG-WERKS
        public string MovementType { get; set; }      // MSEG-BWART
        public string StorageLocation { get; set; }   // MSEG-LGORT
        public string ValuationType { get; set; }     // MSEG-BWTAR
        public decimal Quantity { get; set; }         // SUM(MSEG-MENGE)
        public string Unit { get; set; }              // external unit (T006B-MSEH3)
        public decimal Amount { get; set; }           // SUM(MSEG-DMBTR)
        public string Currency { get; set; }          // MSEG-WAERS
        public string Item { get; set; }              // MSEG-ZEILE
        public string DocYear { get; set; }           // MSEG-MJAHR
        public string PurchaseOrder { get; set; }     // MSEG-EBELN
        public string MaterialDoc { get; set; }       // MSEG-MBLNR
        public string Period { get; set; }            // SPMON (yyyymm)
        public string ValuationClass { get; set; }    // MBEW-BKLAS
        public string ProfitCenter { get; set; }      // MARC-PRCTR
        public string SpecialProcurement { get; set; }// MARC-SOBSL
    }

    /// <summary>
    /// Selection-screen (PAR01) inputs. Multi-value fields accept comma-separated
    /// lists (the SAP fields are SELECT-OPTIONS). Posting date is a range.
    /// </summary>
    public class MaterialDocQuery
    {
        public string Mjahr { get; set; }        // so_leto   (OBLIGATORY)
        public string Werks { get; set; }        // so_werks  (single, default 1061)
        public string Bukrs { get; set; }        // so_bukrs  (default 1060)
        public string BudatFrom { get; set; }    // so_budat low
        public string BudatTo { get; set; }      // so_budat high
        public string Matnr { get; set; }        // so_matnr
        public string Bwart { get; set; }        // so_bwart
        public string Lgort { get; set; }        // so_lgort
        public string Aufnr { get; set; }        // so_order
        public string Lifnr { get; set; }        // so_lifnr
        public string Sobkz { get; set; }        // so_sobkz
        public string Ebeln { get; set; }        // so_ebeln
        public string Bklas { get; set; }        // so_bklas  (MBEW, filtered in .NET)
        public string Prctr { get; set; }        // so_prctr  (MARC, filtered in .NET)
        public string Sobsl { get; set; }        // so_sobsl  (MARC, filtered in .NET)
        public bool Summary { get; set; }        // so_xchpf
        public string Lang { get; set; }         // text/unit language (SL/EN)
    }

    public class MaterialDocResult
    {
        public bool Summary { get; set; }
        public int Count { get; set; }
        public List<MaterialDocRowDto> Rows { get; set; }
        public List<MaterialDocSummaryRowDto> SummaryRows { get; set; }
    }

    /// <summary>
    /// Selection inputs for the Izmet/SO ratio report. Same MSEG-level scope as
    /// <see cref="MaterialDocQuery"/> (year, plant, company, posting-date range);
    /// the SO vs Izmet classification is fixed (see <c>SapMaterialDocService</c>).
    /// </summary>
    public class IzmetRatioQuery
    {
        public string Mjahr { get; set; }        // so_leto   (OBLIGATORY)
        public string Werks { get; set; }        // so_werks  (default 1061)
        public string Bukrs { get; set; }        // so_bukrs  (default 1060)
        public string BudatFrom { get; set; }    // so_budat low
        public string BudatTo { get; set; }      // so_budat high
        public string Prctr { get; set; }        // MARC-PRCTR restriction (the Excel export was prctr 11032001)
        public string Lang { get; set; }         // unit-text language (SL/EN)
    }

    /// <summary>
    /// One day of the Izmet (scrap) vs SO (shop output) report. Amounts are the
    /// signed MSEG-DMBTR sums (the pivot measure "Vsota od Znes.v dom.val.").
    /// </summary>
    public class IzmetDayRowDto
    {
        public string PostingDate { get; set; }   // BUDAT_MKPF (yyyy-MM-dd)
        public decimal ShopOutput { get; set; }    // SO HP  pivot  (BKLAS 4100 / 101,102 / 0027)
        public decimal Izmet { get; set; }         // Izmet HP pivot (BKLAS 3100 / 551,552 / 0013,0016)
        public decimal Ratio { get; set; }         // Izmet / ShopOutput (fraction; 0 when SO is 0)
        public decimal RatioPercent { get; set; }  // Ratio * 100
    }

    /// <summary>Result of the Izmet/SO ratio report: per-day rows plus the period totals.</summary>
    public class IzmetRatioResult
    {
        public decimal ShopOutputTotal { get; set; }
        public decimal IzmetTotal { get; set; }
        public decimal Ratio { get; set; }         // IzmetTotal / ShopOutputTotal
        public decimal RatioPercent { get; set; }
        public List<IzmetDayRowDto> Days { get; set; }
    }

    /// <summary>Raised when a query would return more rows than the safety cap allows.</summary>
    public class MaterialDocTooLargeException : System.Exception
    {
        public MaterialDocTooLargeException(string message) : base(message) { }
    }
}
