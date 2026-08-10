using System.Collections.Concurrent;
using Humans.Application.Interfaces.Holded;

namespace Humans.Infrastructure.Services.Holded;

public sealed class HoldedCallLog : IHoldedCallLog
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
