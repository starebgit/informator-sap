namespace InformatorSAP.Models
{
    public class StockTermConfig
    {
        public int TermId { get; set; }
        public string ContainsText { get; set; }
        public string ExactText { get; set; }
        public string Werks { get; set; }
        public string Lgort { get; set; }
        public int UnitId { get; set; }
        public int? SubunitId { get; set; }
        public string Title { get; set; }
        public bool IsActive { get; set; }
    }
}
