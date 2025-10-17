using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace InformatorSAP.Classes
{
    public class OrderDto
    {
        public string OrderNumber;
        public string MaterialNumber;
        public string MaterialText;
        public decimal StandardQty;
        public decimal YieldQty;
        public string Unit;
    }
}