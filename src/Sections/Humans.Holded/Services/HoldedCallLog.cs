using System.Collections.Concurrent;

namespace Humans.Holded.Services;

internal sealed class HoldedCallLog : IHoldedCallLog
{
    private readonly ConcurrentQueue<HoldedApiCallRecord> _queue = new();

    public void Record(HoldedApiCallRecord record) => _queue.Enqueue(record);

    public IReadOnlyList<HoldedApiCallRecord> DrainAll()
    {
        var records = new List<HoldedApiCallRecord>();
        while (_queue.TryDequeue(out var record))
            records.Add(record);
        return records;
    }
}
