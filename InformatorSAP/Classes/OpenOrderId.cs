// add fields (or create them if OpenOrderId is new)
public class OpenOrderId
{
    public string Aufnr { get; set; }
    public string AufnrDisplay { get; set; }

    // NEW (optional when includeDisplayInfo=true)
    public decimal? Quantity { get; set; }
    public string Unit { get; set; }
    public decimal? Delivered { get; set; }
    public string DeliveredUnit { get; set; }
}
