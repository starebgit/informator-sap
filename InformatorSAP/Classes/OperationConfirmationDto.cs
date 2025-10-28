public class OperationConfirmationDto
{
    public string Operation { get; set; }
    public string ConfirmationRaw { get; set; }       // e.g., 0040969683
    public string ConfirmationDisplay { get; set; }   // e.g., 40969683
}
