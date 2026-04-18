using System.Diagnostics;

namespace SNUS_klk1.Core;

using System.Xml.Linq;
using Logging;
using Models;

public class ProcessingSystem
{
    // eventovi
    public event Action<Job, int> JobCompleted;
    public event Action<Job, Exception> JobFailed;

    private readonly int _maxQueueSize;
    private readonly PriorityQueue<Job, int> _queue = new();
    private readonly SemaphoreSlim _queueSemaphore = new(0);
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _executedJobIds = new();
    private readonly Dictionary<Guid, TaskCompletionSource<int>> _pendingHandles = new();
    private readonly CancellationTokenSource _cts = new();
    private static readonly Random Rng = new();
    private readonly List<(Job Job, int Result, TimeSpan Duration)> _completedJobs = new();
    private readonly List<(Job Job, TimeSpan Duration)> _failedJobs = new();
    private readonly Logger _logger;
    private readonly ReportGenerator _reportGenerator;


    public ProcessingSystem(string configPath, bool startWorkers = true)
    {
        _reportGenerator = new ReportGenerator(this);
        _logger = new Logger($"jobs_{Guid.NewGuid()}.log");

        
        var doc = XDocument.Load(configPath);
        
        int workerCount = int.Parse(doc.Root.Element("WorkerCount").Value);
        _maxQueueSize   = int.Parse(doc.Root.Element("MaxQueueSize").Value);

        // ucitava poslove iz sysConfig xmla
        var initialJobs = doc.Root.Element("Jobs")?.Elements("Job").Select(j => new Job
        {
            Id       = Guid.NewGuid(),
            Type     = Enum.Parse<JobType>(j.Attribute("Type").Value),
            Payload  = j.Attribute("Payload").Value,
            Priority = int.Parse(j.Attribute("Priority").Value)
        });
        
        if (initialJobs != null)
            foreach (var job in initialJobs)
                EnqueueInternal(job);

        // sub na eventove sa lambda izrazima
        JobCompleted += async (job, result) =>
            await _logger.LogCompletedAsync(job, result);

        JobFailed += async (job, ex) =>
            await _logger.LogFailedAsync(job, ex);

        // pokrece worker threadove
        if (startWorkers)
            for (int i = 0; i < workerCount; i++)
                Task.Run(() => WorkerLoop(_cts.Token));
    }
    
    public JobHandle? Submit(Job job)
    {
        var tcs = new TaskCompletionSource<int>();

        lock (_lock)
        {
            if (_queue.Count >= _maxQueueSize)
                return null;

            // odbija duplikate
            if (_pendingHandles.ContainsKey(job.Id) || _executedJobIds.Contains(job.Id))
                return null;

            _pendingHandles[job.Id] = tcs;
            _queue.Enqueue(job, job.Priority);
        }

        _queueSemaphore.Release();
        return new JobHandle { Id = job.Id, Result = tcs.Task };
    }
    
    private async Task WorkerLoop(CancellationToken ct)
    {
        // task koji simulira radnika, prosledjuje se CancellationToken da se mogu svi radnici zaustaviti bezbedno
        // svaka iteracija je jedan posao procesuiran
        while (!ct.IsCancellationRequested)
        {
            await _queueSemaphore.WaitAsync(ct); // radnik ovde ceka poziv submit da primi posao

            Job? job;
            TaskCompletionSource<int>? tcs;

            lock (_lock)
            {
                if (_queue.Count == 0) continue; 
                _queue.TryDequeue(out job, out _); // izvlaci iz reda posao sa najvecim prioritetom
                _pendingHandles.TryGetValue(job.Id, out tcs); // uzima tcs koji je napravljen prilikom submit da se moze rezultat postaviti kasnije

                _executedJobIds.Add(job.Id);
            }

            await ExecuteWithRetryAsync(job, tcs); // poziva se izvan locka
        }
    }
    
    private async Task ExecuteWithRetryAsync(Job job, TaskCompletionSource<int> tcs)
    {
        // metoda koja pokrece izvrsavanje posla i proverava da li je doslo do timeouta
        // pokusava maxAttempts puta i ide do timeoutMilliseconds svaki attempt
        // wrapper metoda za ExecuteJob
        
        const int timeoutMilliseconds = 2000;
        const int maxAttempts = 3;
        var stopwatch = Stopwatch.StartNew();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var executeTask   = ExecuteJob(job);
                var completedTask = await Task.WhenAny(executeTask, Task.Delay(timeoutMilliseconds));

                if (completedTask == executeTask)
                {
                    stopwatch.Stop();
                    int result = await executeTask;

                    lock (_lock)
                        _completedJobs.Add((job, result, stopwatch.Elapsed));

                    JobCompleted.Invoke(job, result);
                    tcs.SetResult(result);
                    return;
                }

                throw new TimeoutException($"Job {job.Id} exceeded 2 seconds on attempt {attempt}");
            }
            catch (Exception ex)
            {
                if (attempt >= maxAttempts)
                {
                    stopwatch.Stop();

                    lock (_lock)
                        _failedJobs.Add((job, stopwatch.Elapsed));

                    await _logger.LogAbortedAsync(job);
                    JobFailed(job, ex);
                    tcs.SetException(ex);
                    return;
                }
            }
        }
    }
    
    private Task<int> ExecuteJob(Job job) => job.Type switch 
    {
        // na osnovu tipa posla poziva ili ExecutePrime ili ExecuteIO

        JobType.Prime => ExecutePrime(job),
        JobType.IO    => ExecuteIo(job),
        _             => throw new ArgumentException($"Unknown job type: {job.Type}")
    };

    private async Task<int> ExecutePrime(Job job)
    {
        // izvlace se vrednosti iz payloada
        var parts = job.Payload.Split(',');
        int upperBound  = int.Parse(parts[0].Split(':')[1].Replace("_", ""));
        int threadCount = Math.Clamp(int.Parse(parts[1].Split(':')[1]), 1, 8); // threadova mora biti izmedju 1 i 8
        int primeCount  = 0;
        int rangeSize   = Math.Max(1, (upperBound - 2) / threadCount); // opseg je [2, upperBound], 0 i 1 nisu prosti, deli se na broj threadova
        
        // pravi task za svaki thread i dodeljuje mu deo opsega
        var tasks = Enumerable.Range(0, threadCount).Select(i =>
        {
            int start = 2 + i * rangeSize;
            int end   = (i == threadCount - 1) ? upperBound : start + rangeSize - 1; // poslednji thread uvek ide do upperBound zbog slucajeva kada postoje brojevi koji preostanu prilikom deljenja na threadove

            // thread safe brojanje, prvo sve u local pa tek posle u primeCount koristeci Interlocked.Add
            return Task.Run(() =>
            {
                int local = 0;
                for (int n = start; n <= end; n++)
                    if (IsPrime(n)) local++;
                Interlocked.Add(ref primeCount, local);
            });
        });
        // ceka da svi taskovi zavrse i onda vrati konacan rezultat
        await Task.WhenAll(tasks);
        return primeCount;
    }

    private Task<int> ExecuteIo(Job job) => Task.Run(() =>
    {
        // kao u specifikaciji cita se delay, thread.sleep(delay) i vraca nasumican broj od 1 do 100
        int delay = int.Parse(job.Payload.Split(':')[1].Replace("_", ""));
        Thread.Sleep(delay); 
        return Rng.Next(0, 101);
    });

    private static bool IsPrime(int n)
    {
        // helper funkcija da proveri da li je prost broj
        if (n < 2) return false;
        if (n == 2) return true;
        if (n % 2 == 0) return false;
        for (int i = 3; i * i <= n; i += 2)
            if (n % i == 0) return false;
        return true;
    }

    private void EnqueueInternal(Job job)
    {
        // koristi ga samo konstruktor da ucita iz sysConfig xml fajla 
        lock (_lock)
        {
            if (_queue.Count < _maxQueueSize)
            {
                _pendingHandles[job.Id] = new TaskCompletionSource<int>();
                _queue.Enqueue(job, job.Priority);
                _queueSemaphore.Release();
            }
        }
    }
    
    public IEnumerable<Job> GetTopJobs(int n)
    {
        // vraca prvih n poslova na cekanju trenutno, sortiranih po prioritetu
        lock (_lock)
        {
            return _queue.UnorderedItems
                .OrderBy(x => x.Priority)
                .Take(n)
                .Select(x => x.Element)
                .ToList();
        }
    }
    // pretraga poslova po ID vrednosti
    public Job? GetJob(Guid id)
    {
        lock (_lock)
        {
            return _queue.UnorderedItems.Select(x => x.Element).FirstOrDefault(j => j.Id == id);
        }
    }
    public (List<(Job, int, TimeSpan)> Completed, List<(Job, TimeSpan)> Failed) GetJobSnapshot()
    {
        // koristi ga reportGenerator, vraca kopije lista uspesnih i neuspesnih poslova
        // kopije da ne bi doslo do iterativnog citanja liste dok drugi thread upisuje u nju
        lock (_lock)
        {
            return (
                new List<(Job, int, TimeSpan)>(_completedJobs),
                new List<(Job, TimeSpan)>(_failedJobs)
            );
        }
    }
    
    public void Stop()
    {
        // poziva se iz glavnog programa kad se pritisne Enter, zaustavlja radnike i report generator
        _cts.Cancel();
        _reportGenerator.Stop();
    }
    
    
}