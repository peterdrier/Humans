using NodaTime;

namespace Humans.Development.Services.Demo;

/// <summary>The last demo run, held in memory: one server, and the report only matters until the next run.</summary>
internal sealed class DemoRunStatus
{
    private readonly Lock _gate = new();
    private IReadOnlyList<string> _lines = [];

    public bool IsRunning { get; private set; }
    public Instant? FinishedAt { get; private set; }

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines; }
    }

    /// <summary>False when a run is already going.</summary>
    public bool TryStart()
    {
        lock (_gate)
        {
            if (IsRunning) return false;
            IsRunning = true;
            _lines = ["Started."];
            return true;
        }
    }

    public void Finish(IReadOnlyList<string> lines, Instant at)
    {
        lock (_gate)
        {
            _lines = [.. lines];
            FinishedAt = at;
            IsRunning = false;
        }
    }
}
