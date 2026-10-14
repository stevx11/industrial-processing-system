namespace SNUS_klk1;

using System.Xml.Linq;
using Core;
using Models;

class Program
{
    private static readonly Random Rng = new();

    static async Task Main()
    {
        var system = new ProcessingSystem("SystemConfig.xml");

        var doc           = XDocument.Load("SystemConfig.xml");
        int producerCount = int.Parse(doc.Root.Element("WorkerCount").Value);

        Console.WriteLine($"Starting {producerCount} producer threads...");

        var cts       = new CancellationTokenSource();
        var producers = Enumerable.Range(0, producerCount)
            .Select(i => Task.Run(() => ProducerLoop(system, i, cts.Token)))
            .ToList();

        Console.WriteLine("Press Enter to stop.");
        Console.ReadLine();

        cts.Cancel();
        system.Stop();

        try
        {
            await Task.WhenAll(producers);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("System stopped.");
    }

    private static async Task ProducerLoop(ProcessingSystem system, int producerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job    = GenerateRandomJob();
                var handle = system.Submit(job);

                if (handle == null)
                    Console.WriteLine($"[Producer {producerId}] Queue full — job {job.Id} rejected.");
                else
                {
                    Console.WriteLine($"[Producer {producerId}] Submitted {job.Type} job {job.Id} (priority {job.Priority}).");

                    _ = handle.Result.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                            Console.WriteLine($"[Producer {producerId}] Job {job.Id} result: {t.Result}");
                        else
                            Console.WriteLine($"[Producer {producerId}] Job {job.Id} failed: {t.Exception?.InnerException?.Message}");
                    });
                }

                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Producer {producerId}] Unexpected error: {ex.Message}");
            }
        }
    }

    private static Job GenerateRandomJob()
    {
        var type    = Rng.Next(2) == 0 ? JobType.Prime : JobType.IO;
        string payload = type == JobType.Prime
            ? $"numbers:{Rng.Next(1000, 100000)},threads:{Rng.Next(1, 9)}"
            : $"delay:{Rng.Next(100, 3000)}";

        return new Job
        {
            Id       = Guid.NewGuid(),
            Type     = type,
            Payload  = payload,
            Priority = Rng.Next(1, 11)
        };
    }
}
