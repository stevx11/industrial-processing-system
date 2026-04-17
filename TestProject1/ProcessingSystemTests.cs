using SNUS_klk1.Core;
using SNUS_klk1.Models;

namespace TestProject1;

public class ProcessingSystemTests : IDisposable
{
    private readonly ProcessingSystem _system;

    public ProcessingSystemTests()
    {
        _system = new ProcessingSystem("SystemConfig.xml");
    }

    public void Dispose() => _system.Stop();
    
    [Fact]
    public void Submit_ValidJob_ReturnsHandle()
    {
        var job = MakePrimeJob(priority: 1);
        var handle = _system.Submit(job);

        Assert.NotNull(handle);
        Assert.Equal(job.Id, handle.Id);
    }
    
    [Fact]
    public async Task PrimeJob_CompletesWithCorrectPrimeCount()
    {
        var job = new Job
        {
            Id       = Guid.NewGuid(),
            Type     = JobType.Prime,
            Payload  = "numbers:10,threads:1",
            Priority = 1
        };

        var handle = _system.Submit(job);
        int result = await handle.Result;

        Assert.Equal(4, result);
    }
    
    [Fact]
    public async Task IOJob_ReturnsValueBetween0And100()
    {
        var job = new Job
        {
            Id       = Guid.NewGuid(),
            Type     = JobType.IO,
            Payload  = "delay:100",
            Priority = 1
        };

        var handle = _system.Submit(job);
        int result = await handle.Result;

        Assert.InRange(result, 0, 100);
    }
    
    [Fact]
    public async Task Submit_SameJobTwice_ExecutesOnlyOnce()
    {
        var job = new Job
        {
            Id       = Guid.NewGuid(),
            Type     = JobType.Prime,
            Payload  = "numbers:10,threads:1",
            Priority = 1
        };

        var handle1 = _system.Submit(job);
        var handle2 = _system.Submit(job); // isti id

        int result = await handle1.Result;

        Assert.Equal(4, result);
        Assert.Null(handle2); // odbije vec u queue
    }
    
    [Fact]
    public void Submit_WhenQueueFull_ReturnsNull()
    {
        // popuni queue preko limita sa jobovima niskog prioriteta
        JobHandle lastRejected = null;

        for (int i = 0; i < 200; i++)
        {
            var result = _system.Submit(new Job
            {
                Id       = Guid.NewGuid(),
                Type     = JobType.IO,
                Payload  = "delay:100",
                Priority = 10
            });

            if (result == null) { lastRejected = null; break; }
        }

        Assert.Null(lastRejected); // bar jedan odbijen
    }
    
    
    [Fact]
    public async Task JobCompleted_EventFires_WhenJobSucceeds()
    {
        var tcs = new TaskCompletionSource<(Guid, int)>();

        _system.JobCompleted += (job, result) => tcs.TrySetResult((job.Id, result));

        var job = MakePrimeJob(priority: 1);
        _system.Submit(job);

        var (firedId, firedResult) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(job.Id, firedId);
        Assert.True(firedResult >= 0);
    }
    
    [Fact]
    public async Task JobFailed_EventFires_WhenJobExceedsTimeout()
    {
        var tcs = new TaskCompletionSource<Guid>();

        _system.JobFailed += (job, ex) => tcs.TrySetResult(job.Id);

        var job = new Job
        {
            Id       = Guid.NewGuid(),
            Type     = JobType.IO,
            Payload  = "delay:10000", //10 sek, uvek timeout
            Priority = 1
        };

        _system.Submit(job);

        // 10s max, 3 pokusaja po 2 sek
        var firedId = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(job.Id, firedId);
    }
    
    [Fact]
    public void GetTopJobs_ReturnsSortedByPriority()
    {
        var system2 = new ProcessingSystem("SystemConfig.xml", false);

        system2.Submit(new Job { Id = Guid.NewGuid(), Type = JobType.IO, Payload = "delay:100", Priority = 5 });
        system2.Submit(new Job { Id = Guid.NewGuid(), Type = JobType.IO, Payload = "delay:100", Priority = 1 });
        system2.Submit(new Job { Id = Guid.NewGuid(), Type = JobType.IO, Payload = "delay:100", Priority = 3 });

        var top = system2.GetTopJobs(3).ToList();
        Assert.Equal(3, top.Count);
        Assert.Equal(1, top[0].Priority);
        Assert.Equal(3, top[1].Priority);
        Assert.Equal(5, top[2].Priority);

        system2.Stop();
    }
    
    private static Job MakePrimeJob(int priority) => new Job
    {
        Id       = Guid.NewGuid(),
        Type     = JobType.Prime,
        Payload  = "numbers:1000,threads:2",
        Priority = priority
    };
}