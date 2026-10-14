using SNUS_klk1.Models;

namespace SNUS_klk1.Logging;

public class Logger
{
    private readonly string _logFile;
    private readonly SemaphoreSlim _logLock = new(1, 1);

    public Logger(string logFile = "jobs.log")
    {
        _logFile = logFile;
    }

    public async Task LogCompletedAsync(Job job, int result) =>
        await WriteAsync($"[{DateTime.Now}] [COMPLETED] {job.Id}, {result}");

    public async Task LogFailedAsync(Job job, Exception ex) =>
        await WriteAsync($"[{DateTime.Now}] [FAILED] {job.Id}, {ex.Message}");

    public async Task LogAbortedAsync(Job job) =>
        await WriteAsync($"[{DateTime.Now}] [ABORT] {job.Id}");

    private async Task WriteAsync(string line)
    {
        await _logLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_logFile, line + Environment.NewLine);
        }
        finally
        {
            _logLock.Release();
        }
    }
}