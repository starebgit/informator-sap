using System;

namespace InformatorSAP.Models
{
    /// <summary>
    /// One target ("cilj") for a single SAP graph. Kept deliberately simple: one current
    /// value per graph, identified by a stable string key (e.g. "izmet_so_ratio",
    /// "izmet_termo_so", "izmet_55_value"). Set by Robi (zorjanr) from the graph card.
    /// </summary>
    public class SapGraphGoalDto
    {
        public string GraphKey { get; set; }
        public decimal GoalValue { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class SetSapGraphGoalRequest
    {
        public string GraphKey { get; set; }
        public decimal? GoalValue { get; set; }
        public string UpdatedBy { get; set; }
    }
}
