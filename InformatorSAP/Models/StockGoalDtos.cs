using System;

namespace InformatorSAP.Models
{
    public class StockGoalDto
    {
        public long Id { get; set; }
        public int TermId { get; set; }
        public decimal GoalValue { get; set; }
        public DateTime ValidFrom { get; set; }
        public DateTime ValidTo { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreateStockGoalRequest
    {
        public int? TermId { get; set; }
        public decimal? GoalValue { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
    }
}
