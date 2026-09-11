using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// Singleton caching decorator for <see cref="IWorkgroupService"/> (design-rules §15): the
/// whole register is one cached snapshot, and every write clears it. A few dozen groups
/// rebuild in milliseconds, so there is nothing to gain from finer-grained invalidation.
/// </summary>
/// <remarks>
/// Nothing time-derived is cached — <see cref="WorkgroupRhythm"/> computes the badges from
/// the snapshot against the caller's clock — so a cached register can never show a stale
/// "update due". Carries <see cref="IUserDataContributor"/> and <see cref="IUserMerge"/>
/// because erasure and the merge fold change rows the cache holds.
/// </remarks>
internal sealed class CachingWorkgroupService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingWorkgroupService> logger)
    : IWorkgroupService, IUserDataContributor, IUserMerge
{
    /// <summary>
    /// DI service key under which the undecorated inner <see cref="IWorkgroupService"/> is
    /// registered. The Singleton decorator resolves the Scoped inner per call.
    /// </summary>
    public const string InnerServiceKey = "workgroups-inner";

    /// <summary>One entry: the whole register. The key is a constant because there is only ever one.</summary>
    private const byte RegisterKey = 0;

    private readonly TrackedCache<byte, IReadOnlyList<WorkgroupInfo>> _cache = new(
        "Workgroups.Register", warmOnStartup: false, logger);

    /// <summary>Diagnostics surface for <c>/Debug/CacheStats</c>.</summary>
    public ICacheStats RegisterCacheStats => _cache;

    // ── Reads ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default)
    {
        if (_cache.TryGet(RegisterKey, out var cached))
            return cached;

        var register = await WithInner(inner => inner.GetRegisterAsync(ct));
        _cache.Set(RegisterKey, register);
        return register;
    }

    // Single-group reads come off the cached register: the graph is already whole, so a
    // second query would only add a round trip.

    public async Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).FirstOrDefault(w => string.Equals(w.Slug, slug, StringComparison.Ordinal));

    public async Task<WorkgroupInfo?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).FirstOrDefault(w => w.Id == id);

    public async Task<IReadOnlyList<WorkgroupInfo>> GetForMemberAsync(Guid userId, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).Where(w => w.IsMember(userId)).ToList();


    // ── Applying and joining ──────────────────────────────────────────────

    public Task<Guid> ApplyAsync(
        Guid actorUserId, WorkgroupApplication application, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ApplyAsync(actorUserId, application, ct));

    public Task JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.JoinAsync(workgroupId, userId, ct));

    public Task LeaveAsync(
        Guid workgroupId,
        Guid userId,
        Guid? replacementCoordinatorUserId,
        bool asAdmin = false,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.LeaveAsync(workgroupId, userId, replacementCoordinatorUserId, asAdmin, ct));

    public Task RequestStatusAsync(
        Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RequestStatusAsync(workgroupId, actorUserId, question, ct));

    // ── Member work ───────────────────────────────────────────────────────

    public Task EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default) =>
        MutateAsync(inner => inner.EditRegisterAsync(workgroupId, actorUserId, edit, ct));

    public Task SetCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        bool asAdmin = false,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.SetCoordinatorsAsync(workgroupId, actorUserId, coordinatorUserIds, asAdmin, ct));

    public Task<Guid> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateMeetingAsync(workgroupId, actorUserId, save, ct));

    public Task UpdateMeetingAsync(
        Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateMeetingAsync(meetingId, actorUserId, save, ct));

    public Task DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeleteMeetingAsync(meetingId, actorUserId, ct));

    public Task<Guid> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AddLogEntryAsync(workgroupId, actorUserId, save, ct));

    public Task UpdateLogEntryAsync(
        Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateLogEntryAsync(entryId, actorUserId, save, ct));

    public Task DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeleteLogEntryAsync(entryId, actorUserId, ct));

    public Task LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.LinkSurveyAsync(workgroupId, actorUserId, surveyId, ct));

    public Task MarkDoneAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDormantReason reason, CancellationToken ct = default) =>
        MutateAsync(inner => inner.MarkDoneAsync(workgroupId, actorUserId, reason, ct));

    // ── Documents ─────────────────────────────────────────────────────────

    public Task<Guid> CreateDocumentAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateDocumentAsync(workgroupId, actorUserId, save, ct));

    public Task UpdateDocumentAsync(
        Guid documentId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateDocumentAsync(documentId, actorUserId, save, ct));

    public Task PublishDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.PublishDocumentAsync(documentId, actorUserId, ct));

    public Task OpenCommentsAsync(
        Guid documentId, Guid actorUserId, WorkgroupCommentWindow window, CancellationToken ct = default) =>
        MutateAsync(inner => inner.OpenCommentsAsync(documentId, actorUserId, window, ct));

    public Task CloseCommentsAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CloseCommentsAsync(documentId, actorUserId, ct));

    public Task DeliverDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeliverDocumentAsync(documentId, actorUserId, ct));

    // ── Comments ──────────────────────────────────────────────────────────

    public Task<Guid> AddCommentAsync(
        Guid documentId, Guid actorUserId, string category, string body, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AddCommentAsync(documentId, actorUserId, category, body, ct));

    public Task RespondToCommentAsync(
        Guid commentId,
        Guid actorUserId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.RespondToCommentAsync(commentId, actorUserId, disposition, response, ct));

    public async Task RespondToCategoryAsync(
        Guid documentId,
        Guid actorUserId,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        await MutateAsync(inner =>
            inner.RespondToCategoryAsync(documentId, actorUserId, category, disposition, response, ct));
    }

    public Task HideCommentAsync(
        Guid commentId, Guid actorUserId, string reason, CancellationToken ct = default) =>
        MutateAsync(inner => inner.HideCommentAsync(commentId, actorUserId, reason, ct));

    // ── The Secretary and the Board ───────────────────────────────────────

    public Task RegisterAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RegisterAsync(workgroupId, actorUserId, ct));

    public Task ReferAsync(
        Guid workgroupId, Guid actorUserId, string? note, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ReferAsync(workgroupId, actorUserId, note, ct));

    public Task RefuseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RefuseAsync(workgroupId, actorUserId, reasons, ct));

    public Task WithdrawAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default) =>
        MutateAsync(inner => inner.WithdrawAsync(workgroupId, actorUserId, reasons, ct));

    public Task CloseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CloseAsync(workgroupId, actorUserId, reasons, ct));

    public Task ReactivateAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ReactivateAsync(workgroupId, actorUserId, ct));

    public Task<Guid> RegisterExistingAsync(
        Guid actorUserId, WorkgroupBootstrap bootstrap, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RegisterExistingAsync(actorUserId, bootstrap, ct));

    public Task RecordDispositionAsync(
        Guid documentId,
        Guid actorUserId,
        WorkgroupDisposition disposition,
        string note,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.RecordDispositionAsync(documentId, actorUserId, disposition, note, ct));

    // ── Settings ──────────────────────────────────────────────────────────

    // The root folder id lives in Settings, which owns its own cache; nothing to hold here.

    public Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default) =>
        WithInner(inner => inner.GetRootDriveFolderIdAsync(ct));

    public Task SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default) =>
        WithInner(inner => inner.SetRootDriveFolderIdAsync(folderId, actorUserId, ct));

    // ── The daily job ─────────────────────────────────────────────────────

    public Task RunDailyRhythmAsync(CancellationToken ct = default) =>
        MutateAsync(inner => inner.RunDailyRhythmAsync(ct));

    // ── IUserDataContributor — GDPR export + erasure ──────────────────────

    // Static table: the erasure-coverage architecture test reads it from an uninitialized
    // instance. Content is kept on purpose (§18) — each note says under what basis.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.WorkgroupMemberships] = null,
            [GdprExportSections.WorkgroupLogEntries] =
                "Partially retained: the authorship is dropped (AuthorUserId nulled), the entry " +
                "itself stays. The log is the register's written record of how a working group " +
                "went about its work — GDPR Art. 17(3)(b) — and is no longer attributable.",
            [GdprExportSections.WorkgroupMeetings] =
                "Partially retained: the creator attribution is dropped; the meeting and its " +
                "minutes stay as the group's record of what it decided.",
            [GdprExportSections.WorkgroupDocuments] =
                "Partially retained: the created-by and updated-by attributions are dropped; the " +
                "document stays, as it may have been delivered to the Board or the Assembly and " +
                "acted on.",
            [GdprExportSections.WorkgroupComments] =
                "Partially retained: the author attribution is dropped; the comment body and the " +
                "group's answer stay. The resolution's obligation to show what was heard and " +
                "decided against is this record, and it is no longer attributable to anyone."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInner(inner => inner.ContributeForUserAsync(userId, ct));

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(inner => inner.EraseForUserAsync(userId, ct));

    // ── IUserMerge — account merge fold ───────────────────────────────────

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        MutateAsync(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));

    // ── Inner-service plumbing ────────────────────────────────────────────

    /// <summary>
    /// A write, followed by a cache clear that happens even when the write throws. A
    /// workflow can persist its row and then fail in a later step (a notification, an audit
    /// entry), and a cache left holding the pre-write register would serve that stale
    /// snapshot until the next successful write.
    /// </summary>
    private async Task MutateAsync(Func<IWorkgroupService, Task> work)
    {
        try
        {
            await WithInner(work);
        }
        finally
        {
            _cache.Clear();
        }
    }

    private async Task<T> MutateAsync<T>(Func<IWorkgroupService, Task<T>> work)
    {
        try
        {
            return await WithInner(work);
        }
        finally
        {
            _cache.Clear();
        }
    }

    private async Task<T> WithInner<T>(Func<IWorkgroupService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IWorkgroupService>(InnerServiceKey);
        return await work(inner);
    }

    private async Task WithInner(Func<IWorkgroupService, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IWorkgroupService>(InnerServiceKey);
        await work(inner);
    }
}
