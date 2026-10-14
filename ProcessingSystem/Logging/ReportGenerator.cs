using System.Xml.Linq;
using SNUS_klk1.Core;

namespace SNUS_klk1.Logging;

public class ReportGenerator
{
    private const string ReportFolder = "reports";
    private const int MaxReports = 10;
    private readonly ProcessingSystem _system;
    private readonly Timer _timer;

    public ReportGenerator(ProcessingSystem system)
    {
        _system = system;
        Directory.CreateDirectory(ReportFolder);
        _timer = new Timer(_ => GenerateReport(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void GenerateReport()
    {
        var (completed, failed) = _system.GetJobSnapshot();
        
        var completedByType = completed
            .GroupBy(x => x.Item1.Type)
            .Select(g => new
            {
                Type        = g.Key,
                Count       = g.Count(),
                AvgDuration = g.Average(x => x.Item3.TotalMilliseconds)
            })
            .OrderBy(x => x.Type);

        var failedByType = failed
            .GroupBy(x => x.Item1.Type)
            .Select(g => new
            {
                Type  = g.Key,
                Count = g.Count()
            })
            .OrderBy(x => x.Type);

        // pravi xml
        var report = new XDocument(
            new XElement("Report",
                new XAttribute("GeneratedAt", DateTime.Now.ToString("o")),
                new XElement("CompletedJobs",
                    completedByType.Select(x =>
                        new XElement("JobType",
                            new XAttribute("Type", x.Type),
                            new XElement("Count", x.Count),
                            new XElement("AvgDurationMs", Math.Round(x.AvgDuration, 2))
                        )
                    )
                ),
                new XElement("FailedJobs",
                    failedByType.Select(x =>
                        new XElement("JobType",
                            new XAttribute("Type", x.Type),
                            new XElement("Count", x.Count)
                        )
                    )
                )
            )
        );

        RotateAndSave(report);
    }

    private void RotateAndSave(XDocument report)
    {
        var files = Directory.GetFiles(ReportFolder, "report_*.xml")
            .OrderBy(f => f)
            .ToList();

        string newFile = Path.Combine(ReportFolder, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.xml");

        if (files.Count >= MaxReports)
        {
            // overwrite najstarijeg
            newFile = files.First();
        }

        report.Save(newFile);
    }

    public void Stop() => _timer.Dispose();
}