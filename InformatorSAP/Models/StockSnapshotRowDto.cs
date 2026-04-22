using System;

namespace InformatorSAP.Models
{
    public class StockSnapshotRowDto
    {
        public long SnapshotId { get; set; }
        public int TermId { get; set; }
        public string Werks { get; set; }
        public string Lgort { get; set; }
        public string Query { get; set; }
        public string ExactText { get; set; }
        public string SearchMode { get; set; }
        public decimal Total { get; set; }
        public int UnitId { get; set; }
        public string Unit { get; set; }
        public decimal PlannedTotal { get; set; }
        public string PlannedUnit { get; set; }
        public decimal DeliveredTotal { get; set; }
        public string DeliveredUnit { get; set; }
        public decimal PlannedMinusDeliveredTotal { get; set; }
        public string PlannedMinusDeliveredUnit { get; set; }
        public DateTime RetrievedAtUtc { get; set; }
        public long? GoalId { get; set; }
        public decimal? GoalValue { get; set; }
        public DateTime? GoalValidFrom { get; set; }
        public DateTime? GoalValidTo { get; set; }
        public DateTime? GoalCreatedAt { get; set; }
        public DateTime? GoalUpdatedAt { get; set; }
    }
}
