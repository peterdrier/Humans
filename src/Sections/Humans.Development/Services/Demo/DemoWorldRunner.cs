using Humans.Base.Demo;
using Humans.Development.Data;
using NodaTime;

namespace Humans.Development.Services.Demo;

/// <summary>
/// Builds or resets the demo world on top of whatever the database already holds. Stories tell in
/// <see cref="IDemoStory.After"/> order and retire in reverse; any section's safety check stops the run first.
/// </summary>
internal sealed class DemoWorldRunner(
    IEnumerable<IDemoStory> stories,
    IEnumerable<IDemoSafetyCheck> safetyChecks,
    IDemoLedgerRepository repository,
    IClock clock,
    ILogger<DemoWorldRunner> logger)
{
    public async Task<IReadOnlyList<string>> RunAsync(bool reset, Guid actorUserId, CancellationToken ct)
    {
        var report = new List<string>();

        foreach (var check in safetyChecks)
        {
            var refusal = await check.RefusalAsync(ct);
            if (refusal is not null)
                report.Add($"Refused: {refusal}");
        }

        if (report.Count > 0)
            return report;

        var ordered = Order(stories);
        var entries = await repository.GetAllAsync(ct);
        var generation = entries.Count == 0 ? 1 : entries.Max(e => e.Generation);

        if (reset)
        {
            // A cut-off run can leave rows still Pending, which retire can't see; topping up first finishes them.
            if (!await TellAllAsync(ordered, new DemoWorld(generation, actorUserId, entries, repository, clock, report), report, ct))
                return report;

            var retiring = new DemoWorld(generation, actorUserId, await repository.GetAllAsync(ct), repository, clock, report);
            foreach (var story in ordered.Reverse())
            {
                if (!await RunStoryAsync(story, s => s.RetireAsync(retiring, ct), "retire", report))
                    return report;
            }

            report.Add($"Generation {generation} retired.");
            generation++;
            entries = await repository.GetAllAsync(ct);
        }

        if (!await TellAllAsync(ordered, new DemoWorld(generation, actorUserId, entries, repository, clock, report), report, ct))
            return report;

        report.Add($"Generation {generation} is up to date ({ordered.Count} stories).");
        return report;
    }

    /// <summary>Throws when a story waits for one nobody registered, or two wait for each other.</summary>
    internal static IReadOnlyList<IDemoStory> Order(IEnumerable<IDemoStory> stories)
    {
        var byKey = stories.ToDictionary(s => s.Key);
        foreach (var story in byKey.Values)
        {
            var missing = story.After.Where(k => !byKey.ContainsKey(k)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Demo story '{story.Key}' runs after '{string.Join("', '", missing)}', which no section registers.");
        }

        var ordered = new List<IDemoStory>();
        var done = new HashSet<DemoStoryKey>();
        var remaining = byKey.Values.OrderBy(s => s.Key.Value, StringComparer.Ordinal).ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(s => s.After.All(done.Contains))
                ?? throw new InvalidOperationException(
                    $"Demo stories wait for each other: {string.Join(", ", remaining.Select(s => s.Key))}.");
            ordered.Add(ready);
            done.Add(ready.Key);
            remaining.Remove(ready);
        }

        return ordered;
    }

    private async Task<bool> TellAllAsync(
        IReadOnlyList<IDemoStory> ordered, DemoWorld world, List<string> report, CancellationToken ct)
    {
        foreach (var story in ordered)
        {
            if (!await RunStoryAsync(story, s => s.TellAsync(world, ct), "tell", report))
                return false;
        }

        return true;
    }

    private async Task<bool> RunStoryAsync(
        IDemoStory story, Func<IDemoStory, Task> step, string verb, List<string> report)
    {
        try
        {
            await step(story);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Demo story {Story} failed to {Verb}", story.Key, verb);
            report.Add($"Story '{story.Key}' failed to {verb}: {ex.Message}. Stopped here.");
            return false;
        }
    }
}
