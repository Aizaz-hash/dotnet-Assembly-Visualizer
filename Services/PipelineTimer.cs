using System.Diagnostics;

namespace DotNetVisualizer.Web.Services;

/// <summary>
/// Records how long each stage of a pipeline took, using the high-resolution <see cref="Stopwatch"/> clock.
/// Stages are measured back to back: <see cref="Mark"/> closes the current stage and starts the next one.
/// </summary>
public sealed class PipelineTimer
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private long _last;
    private readonly List<StageTiming> _stages = new();

    public record StageTiming(string Stage, double Ms);

    /// <summary>Closes the stage that has been running since the previous mark (or the start).</summary>
    public void Mark(string stage)
    {
        long now = _total.ElapsedTicks;
        _stages.Add(new StageTiming(stage, (now - _last) * 1000.0 / Stopwatch.Frequency));
        _last = now;
    }

    public double TotalMs => _total.ElapsedTicks * 1000.0 / Stopwatch.Frequency;

    public IReadOnlyList<StageTiming> Stages => _stages;

    public object ToResult() => new
    {
        totalMs = Math.Round(TotalMs, 2),
        stages = _stages.Select(s => new { stage = s.Stage, ms = Math.Round(s.Ms, 2) }).ToList()
    };
}
