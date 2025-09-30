namespace InformatorSAP.Classes
{
    public class MaterialInfoDto
    {
        public string Code { get; set; }
        public string Name { get; set; }          // MAKT.MAKTX (NAZIV)
        public string OrderNumber { get; set; }   // AFPO.AUFNR (one best match)
    }
}
