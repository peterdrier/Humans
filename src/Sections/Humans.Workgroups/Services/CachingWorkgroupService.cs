using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Workgroups.Services;

/// <summary>
/// The whole register is one snapshot and every write clears it: a few dozen groups rebuild in milliseconds.
/// Nothing time-derived is cached, so a cached register can't show a stale badge.
/// </summary>
internal sealed class CachingWorkgroupService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingWorkgroupService> logger)
    : IWorkgroupService, IUserDataContributor, IUserMerge
{
    public const string InnerServiceKey = "workgroups-inner";

    private const byte RegisterKey = 0;

    private readonly TrackedCache<byte, IReadOnlyList<WorkgroupInfo>> _cache = new(
        "Workgroups.Register", warmOnStartup: false, logger);

    private readonly Lock _cacheGate = new();
    private long _cacheGeneration;

    public ICacheStats RegisterCacheStats => _cache;

    public async Task<IReadOnlyList<WorkgroupInfo>> GetRegisterAsync(CancellationToken ct = default)
    {
        long generation;
        lock (_cacheGate)
        {
            if (_cache.TryGet(RegisterKey, out var cached))
                return cached;
            generation = _cacheGeneration;
        }

        var register = await WithInnerAsync(inner => inner.GetRegisterAsync(ct));
        lock (_cacheGate)
        {
            // A write may have cleared the cache while this register was loading.
            if (generation == _cacheGeneration)
                _cache.Set(RegisterKey, register);
        }
        return register;
    }

    public async Task<WorkgroupInfo?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).FirstOrDefault(w => string.Equals(w.Slug, slug, StringComparison.Ordinal));

    public async Task<WorkgroupInfo?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).FirstOrDefault(w => w.Id == id);

    public async Task<IReadOnlyList<WorkgroupInfo>> GetForMemberAsync(Guid userId, CancellationToken ct = default) =>
        (await GetRegisterAsync(ct)).Where(w => w.IsMember(userId)).ToList();

    public Task<WorkgroupMutationResult<Guid>> ApplyAsync(
        Guid actorUserId, WorkgroupApplication application, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ApplyAsync(actorUserId, application, ct));

    public Task<WorkgroupMutationResult> JoinAsync(Guid workgroupId, Guid userId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.JoinAsync(workgroupId, userId, ct));

    public Task<WorkgroupMutationResult> ApproveMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ApproveMemberAsync(workgroupId, actorUserId, userId, asAdmin, ct));

    public Task<WorkgroupMutationResult> DeclineMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeclineMemberAsync(workgroupId, actorUserId, userId, asAdmin, ct));

    public Task<WorkgroupMutationResult> AddMemberAsync(
        Guid workgroupId, Guid actorUserId, Guid userId, bool asAdmin = false, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AddMemberAsync(workgroupId, actorUserId, userId, asAdmin, ct));

    public Task<WorkgroupMutationResult> LeaveAsync(
        Guid workgroupId,
        Guid userId,
        Guid? replacementCoordinatorUserId,
        bool asAdmin = false,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.LeaveAsync(workgroupId, userId, replacementCoordinatorUserId, asAdmin, ct));

    public Task<WorkgroupMutationResult> RequestStatusAsync(
        Guid workgroupId, Guid actorUserId, string? question, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RequestStatusAsync(workgroupId, actorUserId, question, ct));

    public Task<WorkgroupMutationResult> EditRegisterAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupRegisterEdit edit, CancellationToken ct = default) =>
        MutateAsync(inner => inner.EditRegisterAsync(workgroupId, actorUserId, edit, ct));

    public Task<WorkgroupMutationResult> SetCoordinatorsAsync(
        Guid workgroupId,
        Guid actorUserId,
        IReadOnlyList<Guid> coordinatorUserIds,
        bool asAdmin = false,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.SetCoordinatorsAsync(workgroupId, actorUserId, coordinatorUserIds, asAdmin, ct));

    public Task<WorkgroupMutationResult<Guid>> CreateMeetingAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateMeetingAsync(workgroupId, actorUserId, save, ct));

    public Task<WorkgroupMutationResult> UpdateMeetingAsync(
        Guid meetingId, Guid actorUserId, WorkgroupMeetingSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateMeetingAsync(meetingId, actorUserId, save, ct));

    public Task<WorkgroupMutationResult> DeleteMeetingAsync(Guid meetingId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeleteMeetingAsync(meetingId, actorUserId, ct));

    public Task<WorkgroupMutationResult<Guid>> AddLogEntryAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AddLogEntryAsync(workgroupId, actorUserId, save, ct));

    public Task<WorkgroupMutationResult> UpdateLogEntryAsync(
        Guid entryId, Guid actorUserId, WorkgroupLogEntrySave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateLogEntryAsync(entryId, actorUserId, save, ct));

    public Task<WorkgroupMutationResult> DeleteLogEntryAsync(Guid entryId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeleteLogEntryAsync(entryId, actorUserId, ct));

    public Task<WorkgroupMutationResult> LinkSurveyAsync(
        Guid workgroupId, Guid actorUserId, Guid surveyId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.LinkSurveyAsync(workgroupId, actorUserId, surveyId, ct));

    public Task<WorkgroupMutationResult> MarkDoneAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDormantReason reason, CancellationToken ct = default) =>
        MutateAsync(inner => inner.MarkDoneAsync(workgroupId, actorUserId, reason, ct));

    public Task<WorkgroupMutationResult<Guid>> CreateDocumentAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateDocumentAsync(workgroupId, actorUserId, save, ct));

    public Task<WorkgroupMutationResult> UpdateDocumentAsync(
        Guid documentId, Guid actorUserId, WorkgroupDocumentSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateDocumentAsync(documentId, actorUserId, save, ct));

    public Task<WorkgroupMutationResult> PublishDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.PublishDocumentAsync(documentId, actorUserId, ct));

    public Task<WorkgroupMutationResult> OpenCommentsAsync(
        Guid documentId, Guid actorUserId, WorkgroupCommentWindow window, CancellationToken ct = default) =>
        MutateAsync(inner => inner.OpenCommentsAsync(documentId, actorUserId, window, ct));

    public Task<WorkgroupMutationResult> CloseCommentsAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CloseCommentsAsync(documentId, actorUserId, ct));

    public Task<WorkgroupMutationResult> DeliverDocumentAsync(Guid documentId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeliverDocumentAsync(documentId, actorUserId, ct));

    public Task<WorkgroupMutationResult<Guid>> AddCommentAsync(
        Guid documentId, Guid actorUserId, string category, string body, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AddCommentAsync(documentId, actorUserId, category, body, ct));

    public Task<WorkgroupMutationResult> RespondToCommentAsync(
        Guid commentId,
        Guid actorUserId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.RespondToCommentAsync(commentId, actorUserId, disposition, response, ct));

    public async Task<WorkgroupMutationResult> RespondToCategoryAsync(
        Guid documentId,
        Guid actorUserId,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct = default)
    {
        return await MutateAsync(inner =>
            inner.RespondToCategoryAsync(documentId, actorUserId, category, disposition, response, ct));
    }

    public Task<WorkgroupMutationResult> HideCommentAsync(
        Guid commentId, Guid actorUserId, string reason, CancellationToken ct = default) =>
        MutateAsync(inner => inner.HideCommentAsync(commentId, actorUserId, reason, ct));

    public Task<WorkgroupMutationResult> RegisterAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RegisterAsync(workgroupId, actorUserId, ct));

    public Task<WorkgroupMutationResult> ReferAsync(
        Guid workgroupId, Guid actorUserId, string? note, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ReferAsync(workgroupId, actorUserId, note, ct));

    public Task<WorkgroupMutationResult> RefuseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RefuseAsync(workgroupId, actorUserId, reasons, ct));

    public Task<WorkgroupMutationResult> WithdrawAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default) =>
        MutateAsync(inner => inner.WithdrawAsync(workgroupId, actorUserId, reasons, ct));

    public Task<WorkgroupMutationResult> CloseAsync(
        Guid workgroupId, Guid actorUserId, string reasons, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CloseAsync(workgroupId, actorUserId, reasons, ct));

    public Task<WorkgroupMutationResult> ReactivateAsync(Guid workgroupId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.ReactivateAsync(workgroupId, actorUserId, ct));

    public Task<WorkgroupMutationResult<Guid>> RegisterExistingAsync(
        Guid actorUserId, WorkgroupBootstrap bootstrap, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RegisterExistingAsync(actorUserId, bootstrap, ct));

    public Task<WorkgroupMutationResult<HoldedExpenseAccountRef?>> SetBudgetAsync(
        Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken ct = default) =>
        MutateAsync(inner => inner.SetBudgetAsync(workgroupId, actorUserId, save, ct));

    public Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.ListExpenseAccountsAsync(ct));

    public Task<WorkgroupMutationResult> RecordDispositionAsync(
        Guid documentId,
        Guid actorUserId,
        WorkgroupDisposition disposition,
        string note,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.RecordDispositionAsync(documentId, actorUserId, disposition, note, ct));

    public Task<string?> GetRootDriveFolderIdAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetRootDriveFolderIdAsync(ct));

    public Task<WorkgroupMutationResult> SetRootDriveFolderIdAsync(string folderId, Guid actorUserId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.SetRootDriveFolderIdAsync(folderId, actorUserId, ct));

    public Task RunDailyRhythmAsync(CancellationToken ct = default) =>
        MutateAsync(inner => inner.RunDailyRhythmAsync(ct));

    // Static: the erasure-coverage architecture test reads it from an uninitialized instance.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [WorkgroupService.WorkgroupApplications] =
                "Partially retained: the applicant attribution is dropped (AppliedByUserId " +
                "nulled), the working group itself stays. A registered group is the " +
                "association's own body and outlives whoever proposed it.",
            [WorkgroupService.WorkgroupMemberships] = null,
            [WorkgroupService.WorkgroupLogEntries] =
                "Partially retained: the authorship is dropped (AuthorUserId nulled), the entry " +
                "itself stays. The log is the register's written record of how a working group " +
                "went about its work — GDPR Art. 17(3)(b) — and is no longer attributable.",
            [WorkgroupService.WorkgroupMeetings] =
                "Partially retained: the creator attribution is dropped; the meeting and its " +
                "minutes stay as the group's record of what it decided.",
            [WorkgroupService.WorkgroupDocuments] =
                "Partially retained: the created-by and updated-by attributions are dropped; the " +
                "document stays, as it may have been delivered to the Board or the Assembly and " +
                "acted on.",
            [WorkgroupService.WorkgroupComments] =
                "Partially retained: the author attribution is dropped; the comment body and the " +
                "group's answer stay. The resolution's obligation to show what was heard and " +
                "decided against is this record, and it is no longer attributable to anyone."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync(inner => inner.ContributeForUserAsync(userId, ct));

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(inner => inner.EraseForUserAsync(userId, ct));

    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        MutateAsync(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));

    /// <summary>
    /// Clears even when the write throws: a workflow can persist its row and then fail a later step, and the cache
    /// would otherwise serve the stale register until the next successful write.
    /// </summary>
    private Task MutateAsync(Func<IWorkgroupService, Task> work) =>
        MutateAsync(async inner =>
        {
            await work(inner);
            return true;
        });

    private async Task<T> MutateAsync<T>(Func<IWorkgroupService, Task<T>> work)
    {
        try
        {
            return await WithInnerAsync(work);
        }
        finally
        {
            ClearRegisterCache();
        }
    }

    private void ClearRegisterCache()
    {
        lock (_cacheGate)
        {
            _cacheGeneration++;
            _cache.Clear();
        }
    }

    private async Task<T> WithInnerAsync<T>(Func<IWorkgroupService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IWorkgroupService>(InnerServiceKey);
        return await work(inner);
    }
}
