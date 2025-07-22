using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace InformatorSAP.Classes
{
    public class ComponentDto
    {
        public string Component { get; set; }
        public decimal QuantityRequired { get; set; }
        public string Unit { get; set; }
        public string StorageLocation { get; set; }
        public decimal? AvailableQuantity { get; set; }
    }
}