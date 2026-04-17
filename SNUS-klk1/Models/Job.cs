namespace SNUS_klk1.Models;

public class Job
{
    public Guid Id { get; set; }
    public JobType Type { get; set; }
    public string Payload { get; set; }
    public int Priority { get; set; }
}