using System.Collections.Generic;

namespace InformatorSAP.Models
{
    public class OrderDetailsDto
    {
        public string MaterialCode { get; set; }
        public string Dimension { get; set; }
        public string Name { get; set; }
        public int? Quantity { get; set; }
        public int Code { get; set; }
        public string StartDate { get; set; }
        public string ScheduledEnddate { get; set; }

        public List<PartDto> Parts { get; set; }
        public List<OperationDto> Operations { get; set; }
        public List<UploadDto> Uploads { get; set; }

        public OrderDetailsDto()
        {
            Parts = new List<PartDto>();
            Operations = new List<OperationDto>();
            Uploads = new List<UploadDto>();
        }
    }

    public class PartDto
    {
        public string EgoCode { get; set; }
        public string Name { get; set; }
        public string Dimension { get; set; }
        public string Quantity { get; set; }
    }

    public class OperationDto
    {
        public string Sequence { get; set; }
        public string Label { get; set; }
        public string OperationKey { get; set; }
        public string Name { get; set; }
        public decimal? Norm { get; set; }
        public string Confirmation { get; set; }

        public List<OperationMachineDto> Operation_Machines { get; set; }

        public OperationDto()
        {
            Operation_Machines = new List<OperationMachineDto>();
        }
    }

    public class OperationMachineDto
    {
        public string MachineKey { get; set; }
        public string MachineAltKey { get; set; }
    }

    public class UploadDto
    {
        public int Id { get; set; }
        public string Description { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public int Size { get; set; }
    }
}
