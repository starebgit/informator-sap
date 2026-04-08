public class CooisOrderRowDto
{
    public string Nalog { get; set; }
    public string Material { get; set; }
    public decimal StdKolicina { get; set; }
    public decimal Donos { get; set; }
    public string EM { get; set; }
    public string KratkiTekstMateriala { get; set; }

    // Renamed: combined user/system status text (e.g. "DPOT LANS PMEJ")
    public string StatusSistema { get; set; }
    public string NajZag { get; set; }  // AFKO-GSTRS formatted dd.MM.yyyy
    public string LongText { get; set; }
}
