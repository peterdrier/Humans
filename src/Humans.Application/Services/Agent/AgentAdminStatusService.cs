using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Models;
using NodaTime;

namespace Humans.Application.Services.Agent;

/// <summary>
/// Read-only assembler for the <c>/Agent/Admin/Status</c> view. Pulls one
/// flat per-message projection covering the broadest window (30 days), then
/// computes every other window in memory. At the project's scale (~500
/// users, 90-day retention, sub-thousand messages/day) the projection fits
/// comfortably in RAM — see <c>CLAUDE.md</c> § Scale and Deployment.
/// </summary>
public sealed class AgentAdminStatusService : IAgentAdminStatusService
{
    private readonly IAgentRepository _repo;
    private readonly IAgentSettingsService _settings;
    private readonly IAgentRateLimitStore _rateLimit;
    private readonly IAgentRetentionRunStore _retention;
    private readonly IAgentAnthropicBalanceProvider _balance;
    private readonly IClock _clock;

    public AgentAdminStatusService(
        IAgentRepository repo,
        IAgentSettingsService settings,
        IAgentRateLimitStore rateLimit,
        IAgentRetentionRunStore retention,
        IAgentAnthropicBalanceProvider balance,
        IClock clock)
    {
        _repo = repo;
        _settings = settings;
        _rateLimit = rateLimit;
        _retention = retention;
        _balance = balance;
        _clock = clock;
    }

    public async Task<AgentAdminStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        var window24h = now - Duration.FromDays(1);
        var window7d = now - Duration.FromDays(7);
        var window30d = now - Duration.FromDays(30);

        // Month-to-date in UTC. The agent is admin-internal tooling and the
        // operations team reads the status page knowing the windows are
        // UTC-anchored — matches the rest of the admin diagnostics surface.
        var utcNow = now.InUtc();
        var firstOfMonth = new LocalDate(utcNow.Year, utcNow.Month, 1)
            .AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

        var startOfWindow = firstOfMonth < window30d ? firstOfMonth : window30d;

        var rows = await _repo.ListMessagesSinceAsync(startOfWindow, cancellationToken);

        var settings = _settings.Current;
        var defaultModel = settings.Model;

        // Conversation counts per window come from the parent table — a
        // conversation can sit in a window without producing new messages
        // (rare, but the source-of-truth count must match the table).
        var convCount24h = await _repo.CountConversationsInWindowAsync(window24h, now, cancellationToken);
        var convCount7d = await _repo.CountConversationsInWindowAsync(window7d, now, cancellationToken);
        var convCount30d = await _repo.CountConversationsInWindowAsync(window30d, now, cancellationToken);

        var usage24h = BuildUsage(rows, window24h, convCount24h);
        var usage7d = BuildUsage(rows, window7d, convCount7d);
        var usage30d = BuildUsage(rows, window30d, convCount30d);

        var spend24h = BuildSpend(rows, window24h, defaultModel);
        var spend7d = BuildSpend(rows, window7d, defaultModel);
        var spend30d = BuildSpend(rows, window30d, defaultModel);
        var spendMtd = BuildSpend(rows, firstOfMonth, defaultModel);

        var refusals7d = rows
            .Where(r => r.CreatedAt >= window7d && !string.IsNullOrEmpty(r.RefusalReason))
            .GroupBy(r => r.RefusalReason!, StringComparer.Ordinal)
            .Select(g => new AgentRefusalBucket(g.Key, g.Count()))
            .OrderByDescending(b => b.Count)
            .ToList();
        var refusals7dCount = refusals7d.Sum(b => b.Count);

        var topDocs7d = rows
            .Where(r => r.CreatedAt >= window7d)
            .SelectMany(r => r.FetchedDocs ?? Array.Empty<string>())
            .Where(slug => !string.IsNullOrEmpty(slug))
            .GroupBy(slug => slug, StringComparer.Ordinal)
            .Select(g => new AgentTopDoc(g.Key, g.Count()))
            .OrderByDescending(d => d.Count)
            .Take(10)
            .ToList();

        var topUsers7d = BuildTopUsers(rows, window7d, settings, now);

        var balance = await _balance.GetBalanceAsync(cancellationToken);

        return new AgentAdminStatusReport(
            Usage24h: usage24h,
            Usage7d: usage7d,
            Usage30d: usage30d,
            Spend24h: spend24h,
            Spend7d: spend7d,
            Spend30d: spend30d,
            SpendMtd: spendMtd,
            Refusals7dCount: refusals7dCount,
            Refusals7d: refusals7d,
            TopDocs7d: topDocs7d,
            TopUsers7d: topUsers7d,
            Retention: _retention.Snapshot,
            Balance: balance,
            SettingsStoreWarm: settings.UpdatedAt != Instant.MinValue);
    }

    private static AgentUsageStats BuildUsage(
        IReadOnlyList<AgentStatusMessageRow> rows, Instant since, int conversationCount)
    {
        // Repository returns rows sorted by CreatedAt descending — every
        // message in the smaller-window slice is at the head, so the loop
        // can early-stop once the rows fall outside `since`. Filtering with
        // LINQ Where would also work but the explicit loop keeps the
        // arithmetic readable when summing multiple aggregates together.
        long prompt = 0, output = 0, cached = 0;
        var durations = new List<int>();
        var uniqueUsers = new HashSet<Guid>();

        foreach (var r in rows)
        {
            if (r.CreatedAt < since) break;
            prompt += r.PromptTokens;
            output += r.OutputTokens;
            cached += r.CachedTokens;
            durations.Add(r.DurationMs);
            uniqueUsers.Add(r.UserId);
        }

        var messageCount = durations.Count;
        var cacheBase = prompt + cached;
        var cacheRatio = cacheBase > 0 ? (double)cached / cacheBase : 0.0;
        var avgMs = messageCount > 0 ? (int)durations.Average() : 0;
        var p95Ms = Percentile(durations, 0.95);

        return new AgentUsageStats(
            ConversationCount: conversationCount,
            MessageCount: messageCount,
            UniqueUserCount: uniqueUsers.Count,
            PromptTokens: prompt,
            OutputTokens: output,
            CachedTokens: cached,
            CacheHitRatio: cacheRatio,
            AverageTurnMs: avgMs,
            P95TurnMs: p95Ms);
    }

    private static AgentSpendStats BuildSpend(
        IReadOnlyList<AgentStatusMessageRow> rows, Instant since, string defaultModel)
    {
        // Per-message pricing so a window that spanned a model change still
        // reports correctly. Most production traffic uses one model at a
        // time, but the per-row lookup costs nothing and protects against
        // a future migration window.
        decimal input = 0m, output = 0m, cacheRead = 0m;
        foreach (var r in rows)
        {
            if (r.CreatedAt < since) break;
            var model = string.IsNullOrEmpty(r.Model) ? defaultModel : r.Model;
            var per = AgentPricing.Compute(r.PromptTokens, r.OutputTokens, r.CachedTokens, model);
            input += per.InputUsd;
            output += per.OutputUsd;
            cacheRead += per.CacheReadUsd;
        }
        return new AgentSpendStats(input, output, cacheRead, input + output + cacheRead);
    }

    private List<AgentTopUser> BuildTopUsers(
        IReadOnlyList<AgentStatusMessageRow> rows, Instant since,
        AgentSettingsDto settings, Instant now)
    {
        // Rate-limit store keys by (UserId, LocalDate, Hour) in UTC — see
        // AgentRateLimitHandler for the authoritative usage of these keys.
        var utcNow = now.InUtc();
        var today = utcNow.Date;
        var hour = utcNow.Hour;

        var counts = new Dictionary<Guid, int>();
        foreach (var r in rows)
        {
            if (r.CreatedAt < since) break;
            counts[r.UserId] = counts.TryGetValue(r.UserId, out var c) ? c + 1 : 1;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv =>
            {
                var snapshot = _rateLimit.Get(kv.Key, today, hour);
                var dailyRemaining = Math.Max(0, settings.DailyMessageCap - snapshot.MessagesToday);
                var hourlyRemaining = Math.Max(0, settings.HourlyMessageCap - snapshot.MessagesThisHour);
                return new AgentTopUser(
                    UserId: kv.Key,
                    MessageCount: kv.Value,
                    MessagesTodayRemaining: dailyRemaining,
                    MessagesThisHourRemaining: hourlyRemaining);
            })
            .ToList();
    }

    private static int Percentile(List<int> samples, double p)
    {
        if (samples.Count == 0) return 0;
        // arch:db-sort-ok in-memory percentile over already-materialized list
        samples.Sort();
        var rank = (int)Math.Ceiling(p * samples.Count) - 1;
        if (rank < 0) rank = 0;
        if (rank >= samples.Count) rank = samples.Count - 1;
        return samples[rank];
    }
}
