namespace SNUS_klk1.Models;

public class JobHandle
{
    public Guid Id { get; set; }
    public Task<int> Result { get; set; }
}