using log4net;
using Newtonsoft.Json;

namespace VulnerableDemo;

// A tiny library that really uses the three vulnerable packages, so the visualizer also has
// types, inheritance and calls (including one circular call chain) to show.

public interface IReportWriter
{
    string Write(object data);
}

public class JsonNetReportWriter : IReportWriter
{
    public string Write(object data) => JsonConvert.SerializeObject(data);
}

public class SystemTextJsonReportWriter : IReportWriter
{
    public string Write(object data) => System.Text.Json.JsonSerializer.Serialize(data);
}

public class AuditLogger
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AuditLogger));

    public void Info(string message) => Log.Info(message);
}

public class ReportService
{
    private readonly IReportWriter _writer;
    private readonly AuditLogger _logger = new();

    public ReportService(IReportWriter writer) => _writer = writer;

    public string Generate(object data)
    {
        var text = _writer.Write(data);
        _logger.Info("report generated");
        return Normalizer.Normalize(text);
    }
}

public static class Normalizer
{
    // Normalize -> Validate -> Normalize: a deliberate circular call chain for the Circular Dependencies view.
    public static string Normalize(string text) => Validate(text.Trim());

    public static string Validate(string text) => text.Length > 0 ? Normalize(text.Substring(1)) : text;
}
