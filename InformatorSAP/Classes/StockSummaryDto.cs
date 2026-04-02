namespace InformatorSAP.Classes
{
    public class StockSummaryDto
    {
        public string WERKS { get; set; }
        public string LGORT { get; set; }
        public string Query { get; set; }
        public decimal Total { get; set; }
        public string Unit { get; set; }
        public decimal PlannedTotal { get; set; }
        public string PlannedUnit { get; set; }
        public decimal DeliveredTotal { get; set; }
        public string DeliveredUnit { get; set; }
        public decimal PlannedMinusDeliveredTotal { get; set; }
        public string PlannedMinusDeliveredUnit { get; set; }
    }
}
