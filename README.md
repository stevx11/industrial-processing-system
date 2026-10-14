# Industrial Processing System

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-14-239120?logo=csharp&logoColor=white)
![Tests](https://img.shields.io/badge/tests-xUnit-5E1F87)
![Concurrency](https://img.shields.io/badge/focus-concurrency-orange)

A concurrent, priority-based **job processing engine** in C#. Multiple producers submit jobs to a bounded priority queue, and a pool of asynchronous workers executes them with **timeouts, automatic retries, event-driven logging, and periodic XML reporting**.

The project demonstrates the **producer-consumer pattern**, **async/await**, **thread synchronization primitives**, **parallel computation**, and **unit testing of concurrent code** in modern .NET.

## Architecture

```
  Producers (N)                ProcessingSystem                         Workers (N)
 ┌────────────┐  Submit(job)  ┌─────────────────────────────────┐     ┌─────────────┐
 │ Producer 0 │ ────────────► │  PriorityQueue<Job,int>         │ ──► │  Worker 0   │
 │ Producer 1 │ ◄──────────── │  bounded by MaxQueueSize        │ ──► │  Worker 1   │
 │    ...     │   JobHandle   │  SemaphoreSlim signals workers  │ ──► │    ...      │
 └────────────┘  (Task<int>)  │  Lock guards shared state       │     └──────┬──────┘
                              └─────────────────────────────────┘            │
                                                                             ▼
                                                              ExecuteWithRetryAsync
                                                              2 s timeout × 3 attempts
                                                                             │
                                          ┌──────────────────────────────────┼─────────────┐
                                          ▼                                  ▼             ▼
                                    JobCompleted event              JobFailed event   TaskCompletionSource
                                    → async Logger                  → async Logger    resolves the handle
                                          │
                                          ▼
                                    ReportGenerator (every 60 s)
                                    → reports/report_*.xml (rotating, max 10)
```

1. **Submit.** A producer calls `Submit(job)` and immediately gets back a `JobHandle` whose `Result` is a `Task<int>` it can await. The system rejects the job if the queue is full or the job ID was already seen.
2. **Dispatch.** Workers wait on a `SemaphoreSlim` and dequeue the highest-priority job under a lock.
3. **Execute.** Each job runs with a **2-second timeout** and up to **3 attempts**. On success, the handle resolves with the result. After the final failure, it faults with the exception.
4. **Observe.** `JobCompleted` and `JobFailed` events drive asynchronous file logging. A timer-based report generator writes per-type statistics to XML every minute.

## Job Types

| Type    | Payload example               | What it does                                                                                        |
|---------|-------------------------------|-----------------------------------------------------------------------------------------------------|
| `Prime` | `numbers:50_000,threads:4`    | Counts primes in `[2, N]`, splitting the range across 1–8 parallel tasks and combining counts with `Interlocked.Add` |
| `IO`    | `delay:1_500`                 | Simulates blocking I/O for the given number of milliseconds and returns a value from 0 to 100      |

Priority is an integer where **a lower value means higher priority** (1 = most urgent).

## Features

- **Bounded priority queue** with back-pressure: submissions beyond `MaxQueueSize` are rejected rather than buffered without limit.
- **Idempotent submission**: duplicate job IDs are detected and rejected, so a job never runs twice.
- **Awaitable job handles** built on `TaskCompletionSource<int>`, which decouple submission from completion.
- **Timeout and retry policy** implemented with `Task.WhenAny` and a delay task.
- **Data-parallel prime counting** with per-task local accumulation, which minimizes contention on the shared counter.
- **Event-driven architecture**: logging subscribes to `JobCompleted` and `JobFailed` events instead of being hard-wired into the workers.
- **Thread-safe async logger** that serializes file writes with `SemaphoreSlim(1, 1)`.
- **Periodic XML reports** with LINQ aggregation (count and average duration per job type), capped at 10 rotating files.
- **Graceful shutdown**: a `CancellationToken` stops producers, workers, and the report timer cleanly.
- **Query API**: `GetTopJobs(n)` and `GetJob(id)` inspect pending work without disturbing the queue.
- **XML configuration** for the worker count, queue capacity, and jobs to enqueue at startup.

## Project Structure

```
├── SNUS-klk1/                      # Main application
│   ├── Program.cs                  # Demo: N producer loops generating random jobs
│   ├── SystemConfig.xml            # Workers, queue size, initial jobs
│   ├── Core/
│   │   └── ProcessingSystem.cs     # Queue, workers, retry logic, job execution
│   ├── Models/
│   │   ├── Job.cs                  # Id, Type, Payload, Priority
│   │   ├── JobHandle.cs            # Id + Task<int> Result
│   │   └── JobType.cs              # Prime | IO
│   └── Logging/
│       ├── Logger.cs               # Async, lock-protected file logger
│       └── ReportGenerator.cs      # Timed XML reports with rotation
└── TestProject1/
    └── ProcessingSystemTests.cs    # xUnit tests
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The version is pinned in `global.json`.

### Configuration

`SystemConfig.xml` controls the system:

```xml
<SystemConfig>
  <WorkerCount>4</WorkerCount>
  <MaxQueueSize>100</MaxQueueSize>
  <Jobs>
    <Job Type="Prime" Payload="numbers:10_000,threads:3" Priority="1" />
    <Job Type="IO"    Payload="delay:500"                Priority="3" />
  </Jobs>
</SystemConfig>
```

### Run

```bash
cd SNUS-klk1
dotnet run
```

Producers start submitting random jobs every second, and results stream to the console. Press **Enter** to shut down gracefully.

**Output files**

- `jobs_<guid>.log`: one line per completed, failed, or aborted job
- `reports/report_<timestamp>.xml`: per-type statistics, written every 60 seconds

### Test

```bash
dotnet test TestProject1
```

The xUnit suite covers:

- Job submission returns a handle with a matching ID
- Correct prime counts (e.g., 4 primes up to 10)
- IO job results stay within the 0–100 range
- Duplicate submissions are rejected and the job executes only once
- Back-pressure: submissions are rejected when the queue is full
- `JobCompleted` fires on success
- `JobFailed` fires after the timeout is exhausted across all retries
- `GetTopJobs` returns jobs ordered by priority

## Sample Report

Example of the report format (values are illustrative):

```xml
<Report GeneratedAt="2026-10-14T20:15:00.0000000+02:00">
  <CompletedJobs>
    <JobType Type="Prime">
      <Count>42</Count>
      <AvgDurationMs>18.7</AvgDurationMs>
    </JobType>
    <JobType Type="IO">
      <Count>35</Count>
      <AvgDurationMs>1489.22</AvgDurationMs>
    </JobType>
  </CompletedJobs>
  <FailedJobs>
    <JobType Type="IO">
      <Count>9</Count>
    </JobType>
  </FailedJobs>
</Report>
```

## Roadmap

- Cancel timed-out attempts with a linked `CancellationToken` instead of letting them finish in the background
- Swap `Thread.Sleep` in IO jobs for `Task.Delay` so simulated I/O doesn't block thread-pool threads
- Configurable timeout, retry count, and backoff via `SystemConfig.xml`
- Replace payload string parsing with typed job parameters
- Expose metrics (throughput, queue depth) through an HTTP endpoint or `System.Diagnostics.Metrics`
