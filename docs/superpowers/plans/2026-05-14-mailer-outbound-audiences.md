# Mailer Outbound + Audience Framework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add outbound writes to `IMailerLiteService` (prefix-guarded to `"Humans - "` ML groups), introduce an `IMailerAudience` framework, ship the first audience `TicketNoShiftsAudience`, wire a daily Hangfire sync + on-demand admin button.

**Architecture:** Two layers. The outbound slice adds four write methods to the existing `MailerLiteClient` (singleton, `IHttpClientFactory`-based, with an in-memory cache populated lazily). Every write looks up the target group by id and rejects unless `Name.StartsWith("Humans - ")`. On top, a new `IMailerAudience` primitive (registered as singletons in DI) defines audiences as code; `MailerAudienceSyncService` orchestrates compute → diff → apply → audit. One audience implementation: `TicketNoShiftsAudience` (Valid/CheckedIn matched ticket attendee in active vendor event, no Pending/Confirmed shift signup in active `EventSettings`).

**Tech Stack:** .NET 10, ASP.NET Core MVC, NodaTime, Hangfire (recurring jobs), xUnit + AwesomeAssertions, Humans Clean Architecture (Domain / Application / Infrastructure / Web).

**Spec:** [`docs/superpowers/specs/2026-05-14-mailer-outbound-audiences-design.md`](../specs/2026-05-14-mailer-outbound-audiences-design.md).

**Branch:** `mailer-outbound-audiences` (worktree at `.worktrees/mailer-outbound-audiences`).

---

## File Map

**New files:**
- `src/Humans.Application/Interfaces/Mailer/IMailerAudience.cs` — audience primitive
- `src/Humans.Application/Interfaces/Mailer/IMailerAudienceSyncService.cs` — orchestrator interface
- `src/Humans.Application/Interfaces/Mailer/Dtos/AudienceStats.cs` — dashboard stats record
- `src/Humans.Application/Interfaces/Mailer/Dtos/AudienceSyncResult.cs` — post-sync counts record
- `src/Humans.Application/Interfaces/Mailer/Dtos/BulkImportResult.cs` — ML bulk-import response shape
- `src/Humans.Application/Services/Mailer/MailerAudienceSyncService.cs` — orchestrator impl
- `src/Humans.Application/Services/Mailer/Audiences/TicketNoShiftsAudience.cs` — first audience
- `src/Humans.Infrastructure/Jobs/MailerAudienceSyncJob.cs` — Hangfire recurring job
- `src/Humans.Web/Models/Mailer/AudienceCardRow.cs` — one row of the dashboard card
- `src/Humans.Web/Views/Mailer/Admin/_AudiencesCard.cshtml` — dashboard partial
- `tests/Humans.Application.Tests/Services/Mailer/MailerAudienceSyncServiceTests.cs`
- `tests/Humans.Application.Tests/Services/Mailer/Audiences/TicketNoShiftsAudienceTests.cs`
- `tests/Humans.Web.Tests/Controllers/Mailer/MailerAdminControllerAudienceSyncTests.cs`

**Modified files:**
- `src/Humans.Domain/Enums/AuditAction.cs` — add `MailerLiteAudienceSyncCompleted`
- `src/Humans.Application/Interfaces/Mailer/MailerLiteOptions.cs` — add `AudienceSyncCron`, `BulkImportChunkSize`
- `src/Humans.Application/Interfaces/Mailer/IMailerLiteService.cs` — add four write methods
- `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs` — add `GetActiveCommittedUserIdsForEventAsync`
- `src/Humans.Application/Interfaces/Shifts/IShiftSignupRepository.cs` — add matching repo method
- `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` — implement
- `src/Humans.Infrastructure/Repositories/Shifts/ShiftSignupRepository.cs` — implement
- `src/Humans.Infrastructure/Services/Mailer/MailerLiteClient.cs` — implement four write methods + prefix guard + cache invalidation
- `src/Humans.Web/Controllers/Mailer/MailerAdminController.cs` — add audience-sync POST + extend `Index`
- `src/Humans.Web/Models/Mailer/MailerDashboardViewModel.cs` — add `Audiences`
- `src/Humans.Web/Views/Mailer/Admin/Index.cshtml` — render audiences card
- `src/Humans.Web/Extensions/RecurringJobExtensions.cs` — register `mailer-audience-sync`
- `src/Humans.Web/Program.cs` — DI registrations (`IMailerAudience`, sync service, job)
- `tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs` — replace `HasNoWriteMethods`, add three new pins
- `tests/Humans.Application.Tests/Services/Mailer/MailerLiteClientWriteGuardTests.cs` — replace with new write-guard tests
- `docs/sections/Mailer.md` — update invariants (relax GET-only, add prefix-write, audience framework)
- `docs/sections/AuditLog.md` — list new action

---

## Phase 1: Outbound primitives on `IMailerLiteService`

### Task 1: Add audit action enum value

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1: Locate the existing Mailer action**

Run: `grep -n MailerLiteReconciliationCompleted src/Humans.Domain/Enums/AuditAction.cs`
Expected: one match around line 166.

- [ ] **Step 2: Add the new action immediately after it**

Edit `src/Humans.Domain/Enums/AuditAction.cs` — locate the line:
```csharp
    MailerLiteReconciliationCompleted,
```
Add the next line immediately below it:
```csharp
    MailerLiteAudienceSyncCompleted,
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds (no test failures expected from this change alone — but any code using exhaustive switches over `AuditAction` may surface; if so, add a default case-mapping in the same commit).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "feat(audit): add MailerLiteAudienceSyncCompleted action"
```

---

### Task 2: Add `BulkImportResult` DTO

**Files:**
- Create: `src/Humans.Application/Interfaces/Mailer/Dtos/BulkImportResult.cs`

- [ ] **Step 1: Write the DTO**

Create `src/Humans.Application/Interfaces/Mailer/Dtos/BulkImportResult.cs`:
```csharp
namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>
/// Aggregated outcome of a bulk import call (or chain of chunked calls) that
/// creates-and-assigns subscribers to a single MailerLite group.
/// </summary>
public sealed record BulkImportResult(
    int Created,
    int Updated,
    int Duplicates,
    int Errors);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Mailer/Dtos/BulkImportResult.cs
git commit -m "feat(mailer): add BulkImportResult DTO"
```

---

### Task 3: Extend `MailerLiteOptions`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Mailer/MailerLiteOptions.cs`

- [ ] **Step 1: Add the two new properties**

Edit `src/Humans.Application/Interfaces/Mailer/MailerLiteOptions.cs` to read in full:
```csharp
namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// MailerLite client configuration. Bound from <c>MailerLite:*</c> in
/// configuration (user-secrets in dev, env-var-shaped <c>MailerLite__ApiKey</c>
/// or flat <c>MAILERLITE_API_KEY</c> in PR/prod — see Program.cs binding).
/// </summary>
public sealed class MailerLiteOptions
{
    public const string SectionName = "MailerLite";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://connect.mailerlite.com";
    public string ApiVersion { get; set; } = "2038-01-01";

    /// <summary>Cron expression for the daily audience sync job. Default 06:00 UTC.</summary>
    public string AudienceSyncCron { get; set; } = "0 6 * * *";

    /// <summary>
    /// Max subscribers per call to <c>POST /api/groups/{id}/subscribers/import</c>.
    /// Implementer to verify ML v2 current ceiling; 50 is the documented safe value
    /// observed in 2026-05.
    /// </summary>
    public int BulkImportChunkSize { get; set; } = 50;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Mailer/MailerLiteOptions.cs
git commit -m "feat(mailer): add AudienceSyncCron + BulkImportChunkSize options"
```

---

### Task 4: Replace the architecture pin on `IMailerLiteService`

**Files:**
- Modify: `tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs`

- [ ] **Step 1: Find the existing test**

Run: `grep -n IMailerLiteService_HasNoWriteMethods tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs`
Expected: at least one match.

- [ ] **Step 2: Replace it with the new pin**

Replace the existing `IMailerLiteService_HasNoWriteMethods` test (entire `[HumansFact]` method) with:
```csharp
[HumansFact]
public void IMailerLiteService_OnlyAllowsAudienceWrites()
{
    var allowedWrites = new HashSet<string>
    {
        nameof(IMailerLiteService.CreateGroupAsync),
        nameof(IMailerLiteService.AssignSubscriberToGroupAsync),
        nameof(IMailerLiteService.UnassignSubscriberFromGroupAsync),
        nameof(IMailerLiteService.BulkImportSubscribersToGroupAsync),
    };

    var writePrefixes = new[]
    {
        "Create", "Update", "Delete", "Upsert", "Add", "Remove",
        "Set", "Post", "Put", "Patch", "Assign", "Unassign", "Bulk",
    };

    var unexpectedWrites = typeof(IMailerLiteService).GetMethods()
        .Where(m => writePrefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal)))
        .Where(m => !allowedWrites.Contains(m.Name))
        .Select(m => m.Name)
        .ToList();

    unexpectedWrites.Should().BeEmpty(
        "IMailerLiteService writes are restricted to the four audience-management methods. " +
        "New writes need their own architecture review.");
}
```

- [ ] **Step 3: Run — should fail to compile**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~MailerArchitectureTests" -v quiet`
Expected: compile error — `IMailerLiteService.CreateGroupAsync` and the other three don't exist yet. This is the failing test that locks in the contract for the next task.

- [ ] **Step 4: Commit the failing test**

```bash
git add tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs
git commit -m "test(mailer): replace HasNoWriteMethods with OnlyAllowsAudienceWrites pin"
```

---

### Task 5: Add write method signatures to `IMailerLiteService`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Mailer/IMailerLiteService.cs`

- [ ] **Step 1: Update the interface**

Replace the entire file with:
```csharp
using Humans.Application.Interfaces.Mailer.Dtos;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// MailerLite client surface. Reads cover account summary, groups, and
/// subscribers. Writes are narrow: limited to creating "Humans - "-prefixed
/// groups and managing membership in those groups. Pinned by
/// <c>MailerArchitectureTests.IMailerLiteService_OnlyAllowsAudienceWrites</c>.
/// </summary>
public interface IMailerLiteService : IApplicationService
{
    Task<MailerLiteAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(CancellationToken ct = default);

    IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(CancellationToken ct = default);

    Task<MailerLiteSubscriber?> GetSubscriberAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Creates a new group in MailerLite. Runtime-rejects with
    /// <see cref="InvalidOperationException"/> if <paramref name="name"/> does
    /// not start with <c>"Humans - "</c>.
    /// </summary>
    Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Assigns an existing subscriber to a group. Runtime-rejects with
    /// <see cref="InvalidOperationException"/> if the target group's
    /// <see cref="MailerLiteGroup.Name"/> does not start with <c>"Humans - "</c>.
    /// </summary>
    Task AssignSubscriberToGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);

    /// <summary>
    /// Removes a subscriber from a group. Same prefix guard as assign.
    /// </summary>
    Task UnassignSubscriberFromGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);

    /// <summary>
    /// Bulk-creates-or-updates subscribers from a list of emails and assigns them
    /// to the target group in one MailerLite call (chunked per
    /// <c>MailerLiteOptions.BulkImportChunkSize</c> by the implementation).
    /// Same prefix guard as assign.
    /// </summary>
    Task<BulkImportResult> BulkImportSubscribersToGroupAsync(
        string groupId, IReadOnlyList<string> emails, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build — interface compiles, but client doesn't implement yet**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build FAILS in `Humans.Infrastructure` — `MailerLiteClient` doesn't implement the four new methods.

- [ ] **Step 3: Commit the interface alone**

```bash
git add src/Humans.Application/Interfaces/Mailer/IMailerLiteService.cs
git commit -m "feat(mailer): declare four prefix-guarded write methods on IMailerLiteService"
```

---

### Task 6: Replace the `MailerLiteClientWriteGuardTests` test fixture

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/Mailer/MailerLiteClientWriteGuardTests.cs`

- [ ] **Step 1: Rewrite the test fixture**

Replace the entire file with:
```csharp
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Infrastructure.Services.Mailer;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerLiteClientWriteGuardTests
{
    [HumansFact]
    public async Task CreateGroupAsync_RejectsNonHumansName()
    {
        var client = NewClient(new ScriptedHandler());

        var act = async () => await client.CreateGroupAsync("Newsletter", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
    }

    [HumansFact]
    public async Task AssignSubscriberToGroupAsync_RejectsWritesToNonHumansGroups()
    {
        var handler = new ScriptedHandler();
        // First /api/groups call returns a single non-Humans group; the guard reads it
        // and rejects before any write hits the wire.
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"data":[{"id":"99","name":"Newsletter","created_at":"2026-01-01 00:00:00","active_count":0,"unsubscribed_count":0,"unconfirmed_count":0,"bounced_count":0,"junk_count":0}],"meta":{"current_page":1,"last_page":1}}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"data":[],"meta":{"next_cursor":null}}"""); // for the subscriber pre-populate
        var client = NewClient(handler);

        var act = async () => await client.AssignSubscriberToGroupAsync("sub-1", "99", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
    }

    [HumansFact]
    public async Task UnassignSubscriberFromGroupAsync_RejectsWritesToNonHumansGroups()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"data":[{"id":"99","name":"Newsletter","created_at":"2026-01-01 00:00:00","active_count":0,"unsubscribed_count":0,"unconfirmed_count":0,"bounced_count":0,"junk_count":0}],"meta":{"current_page":1,"last_page":1}}""");
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        var client = NewClient(handler);

        var act = async () => await client.UnassignSubscriberFromGroupAsync("sub-1", "99", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task BulkImportSubscribersToGroupAsync_RejectsWritesToNonHumansGroups()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"data":[{"id":"99","name":"Newsletter","created_at":"2026-01-01 00:00:00","active_count":0,"unsubscribed_count":0,"unconfirmed_count":0,"bounced_count":0,"junk_count":0}],"meta":{"current_page":1,"last_page":1}}""");
        handler.EnqueueJson(HttpStatusCode.OK, """{"data":[],"meta":{"next_cursor":null}}""");
        var client = NewClient(handler);

        var act = async () => await client.BulkImportSubscribersToGroupAsync(
            "99", new[] { "a@example.com" }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static MailerLiteClient NewClient(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NodaTime.SystemClock.Instance,
            NullLogger<MailerLiteClient>.Instance);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public void EnqueueJson(HttpStatusCode status, string body)
            => _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://example.test/") };
    }
}
```

- [ ] **Step 2: Run — should fail to compile (write methods not yet implemented in `MailerLiteClient`)**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~MailerLiteClientWriteGuardTests" -v quiet`
Expected: compile error — methods don't exist on the client yet.

- [ ] **Step 3: Commit the failing tests**

```bash
git add tests/Humans.Application.Tests/Services/Mailer/MailerLiteClientWriteGuardTests.cs
git commit -m "test(mailer): replace write-guard tests with audience-write specifications"
```

---

### Task 7: Implement the four writes in `MailerLiteClient` with prefix guard + cache invalidation

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Mailer/MailerLiteClient.cs`

- [ ] **Step 1: Add a typed Options dependency**

Currently `MailerLiteClient` doesn't take `MailerLiteOptions` — it relies on the `IHttpClientFactory` named client for base URL + auth. We need `BulkImportChunkSize` injected, so update the constructor.

Replace lines 36-41 (the constructor) with:
```csharp
public MailerLiteClient(
    IHttpClientFactory httpFactory,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<MailerLiteOptions> options,
    ILogger<MailerLiteClient> logger)
{
    _httpFactory = httpFactory;
    _clock = clock;
    _options = options.Value;
    _logger = logger;
}
```

Add the field declaration immediately before `_logger`'s declaration (around line 28):
```csharp
private readonly MailerLiteOptions _options;
```

- [ ] **Step 2: Relax the `SendAsync` GET-only guard**

Replace lines 171-202 (the existing `SendAsync` method) with:
```csharp
private async Task<HttpResponseMessage> SendAsync(
    HttpMethod method, string url, HttpContent? content, CancellationToken ct)
{
    var http = _httpFactory.CreateClient(HttpClientName);
    using var req = new HttpRequestMessage(method, url);
    if (content is not null) req.Content = content;
    HttpResponseMessage resp;
    try
    {
        resp = await http.SendAsync(req, ct);
    }
    catch (HttpRequestException ex)
    {
        _logger.LogError(ex, "MailerLite HTTP call failed: {Method} {Url}", method, url);
        throw;
    }
    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
    {
        _logger.LogError(ex, "MailerLite HTTP call timed out: {Method} {Url}", method, url);
        throw;
    }
    if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
        && int.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var remaining) && remaining < 20)
        _logger.LogWarning("MailerLite rate limit remaining: {Remaining}", remaining);
    if (!resp.IsSuccessStatusCode)
        _logger.LogWarning("MailerLite returned {StatusCode}: {Method} {Url}",
            (int)resp.StatusCode, method, url);
    return resp;
}
```

Then update all existing call sites of `SendAsync` (search for `SendAsync(HttpMethod.Get,`) to pass `null` as the new `content` parameter:
```csharp
using var resp = await SendAsync(HttpMethod.Get, url, content: null, ct);
```

Also update `SendForTestsAsync` signature:
```csharp
internal Task<HttpResponseMessage> SendForTestsAsync(HttpMethod method, string url, CancellationToken ct)
    => SendAsync(method, url, content: null, ct);
```

- [ ] **Step 3: Add the prefix guard helper**

Add this private helper at the top of the class (after the constructor):
```csharp
private const string HumansGroupPrefix = "Humans - ";

private async Task<MailerLiteGroup> RequireHumansGroupAsync(string groupId, CancellationToken ct)
{
    var groups = await ListGroupsAsync(ct);
    var group = groups.FirstOrDefault(g => g.Id == groupId)
        ?? throw new InvalidOperationException(
            $"MailerLite group '{groupId}' not found.");
    if (!group.Name.StartsWith(HumansGroupPrefix, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"MailerLite group '{group.Name}' (id={groupId}) is not managed by Humans. " +
            $"Writes are restricted to groups whose name starts with '{HumansGroupPrefix}'.");
    return group;
}
```

- [ ] **Step 4: Implement `CreateGroupAsync`**

Add to the class body (suggested location: after `RefreshAsync`):
```csharp
public async Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default)
{
    if (!name.StartsWith(HumansGroupPrefix, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"Group name '{name}' must start with '{HumansGroupPrefix}'.");

    using var body = JsonContent.Create(new { name }, options: Json);
    using var resp = await SendAsync(HttpMethod.Post, "/api/groups", body, ct);
    resp.EnsureSuccessStatusCode();
    var env = await resp.Content.ReadFromJsonAsync<GroupSingleEnvelope>(Json, ct)
        ?? throw new InvalidOperationException("MailerLite returned empty body on CreateGroup.");

    InvalidateGroupsCache();
    return env.Data;
}
```

Add the envelope record alongside the existing private records at the bottom of the class:
```csharp
private sealed record GroupSingleEnvelope(
    [property: JsonPropertyName("data")] MailerLiteGroup Data);
```

- [ ] **Step 5: Implement `AssignSubscriberToGroupAsync`**

Add to the class body:
```csharp
public async Task AssignSubscriberToGroupAsync(
    string subscriberId, string groupId, CancellationToken ct = default)
{
    await RequireHumansGroupAsync(groupId, ct);

    using var resp = await SendAsync(
        HttpMethod.Post,
        $"/api/subscribers/{Uri.EscapeDataString(subscriberId)}/groups/{Uri.EscapeDataString(groupId)}",
        content: null, ct);
    resp.EnsureSuccessStatusCode();
    InvalidateSubscribersCache();
}
```

- [ ] **Step 6: Implement `UnassignSubscriberFromGroupAsync`**

Add to the class body:
```csharp
public async Task UnassignSubscriberFromGroupAsync(
    string subscriberId, string groupId, CancellationToken ct = default)
{
    await RequireHumansGroupAsync(groupId, ct);

    using var resp = await SendAsync(
        HttpMethod.Delete,
        $"/api/subscribers/{Uri.EscapeDataString(subscriberId)}/groups/{Uri.EscapeDataString(groupId)}",
        content: null, ct);
    resp.EnsureSuccessStatusCode();
    InvalidateSubscribersCache();
}
```

- [ ] **Step 7: Implement `BulkImportSubscribersToGroupAsync`**

Add to the class body:
```csharp
public async Task<BulkImportResult> BulkImportSubscribersToGroupAsync(
    string groupId, IReadOnlyList<string> emails, CancellationToken ct = default)
{
    await RequireHumansGroupAsync(groupId, ct);
    if (emails.Count == 0)
        return new BulkImportResult(0, 0, 0, 0);

    int created = 0, updated = 0, duplicates = 0, errors = 0;
    var chunkSize = Math.Max(1, _options.BulkImportChunkSize);

    foreach (var chunk in emails.Chunk(chunkSize))
    {
        var payload = new
        {
            subscribers = chunk.Select(e => new { email = e }).ToArray(),
            resubscribe = false,        // do not re-subscribe unsubscribed users
            autoresponders = false,     // do not trigger ML automations on import
        };
        using var body = JsonContent.Create(payload, options: Json);
        using var resp = await SendAsync(
            HttpMethod.Post,
            $"/api/groups/{Uri.EscapeDataString(groupId)}/subscribers/import",
            body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            errors += chunk.Length;
            _logger.LogWarning(
                "BulkImport chunk failed for group {GroupId}: {StatusCode}",
                groupId, (int)resp.StatusCode);
            continue;
        }
        var parsed = await resp.Content.ReadFromJsonAsync<BulkImportEnvelope>(Json, ct);
        created += parsed?.Imported ?? 0;
        updated += parsed?.Updated ?? 0;
        duplicates += parsed?.Duplicates ?? 0;
    }

    InvalidateSubscribersCache();
    return new BulkImportResult(created, updated, duplicates, errors);
}
```

Add the envelope record alongside the other private records:
```csharp
private sealed record BulkImportEnvelope(
    [property: JsonPropertyName("imported")] int Imported,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("duplicates")] int Duplicates);
```

> Implementer note: ML v2's actual response envelope for `groups/{id}/subscribers/import` may differ (the documented shape historically uses `import_added` / `import_updated`). Adjust property names + `BulkImportEnvelope` to match current ML docs at impl time. The DTO `BulkImportResult` exposed to callers is stable.

- [ ] **Step 8: Add cache-invalidation helpers**

Add to the class body (after `RefreshAsync`):
```csharp
private void InvalidateSubscribersCache()
{
    _gate.Wait();
    try
    {
        _subscribers = null;
        _summary = null;
    }
    finally { _gate.Release(); }
}

private void InvalidateGroupsCache()
{
    _gate.Wait();
    try { _groups = null; }
    finally { _gate.Release(); }
}
```

- [ ] **Step 9: Update DI registration to provide `IOptions<MailerLiteOptions>`**

Run: `grep -rn "AddSingleton<IMailerLiteService" src/Humans.Web src/Humans.Infrastructure`
Expected: one registration site (likely `Program.cs` or a service-registration extension).

At that site, ensure `services.Configure<MailerLiteOptions>(...)` is already bound (it is — Slice 1 set this up). Nothing else to change; the constructor change in Step 1 is satisfied automatically.

- [ ] **Step 10: Run the write-guard tests**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~MailerLiteClientWriteGuardTests" -v quiet`
Expected: all four tests PASS.

- [ ] **Step 11: Run the architecture pin**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~IMailerLiteService_OnlyAllowsAudienceWrites" -v quiet`
Expected: PASS.

- [ ] **Step 12: Run the full Mailer test suite**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~Mailer" -v quiet`
Expected: all Mailer tests pass, including the Slice 1 import tests (the GET-only guard removal didn't break them — they still go through `SendAsync(HttpMethod.Get, ...)`).

- [ ] **Step 13: Commit**

```bash
git add src/Humans.Infrastructure/Services/Mailer/MailerLiteClient.cs
git commit -m "feat(mailer): implement four prefix-guarded outbound writes on MailerLiteClient"
```

---

## Phase 2: Audience primitive + framework

### Task 8: Define `IMailerAudience`

**Files:**
- Create: `src/Humans.Application/Interfaces/Mailer/IMailerAudience.cs`

- [ ] **Step 1: Write the interface**

Create `src/Humans.Application/Interfaces/Mailer/IMailerAudience.cs`:
```csharp
namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// A code-defined mailing list. Each implementation computes a set of Humans
/// user-ids who belong in the audience and maps it to a single MailerLite group
/// whose <see cref="MailerLiteGroupName"/> must start with "Humans - "
/// (pinned by <c>MailerArchitectureTests.AllAudiences_UseHumansPrefix</c>).
/// </summary>
public interface IMailerAudience
{
    /// <summary>Stable URL-safe key (e.g. "ticket-no-shifts").</summary>
    string Key { get; }

    /// <summary>Display name shown on the dashboard card.</summary>
    string DisplayName { get; }

    /// <summary>Target MailerLite group name. Must start with "Humans - ".</summary>
    string MailerLiteGroupName { get; }

    /// <summary>
    /// Returns the current Humans user-ids who belong in this audience.
    /// Implementations read cross-section state via service interfaces only —
    /// never via <c>HumansDbContext</c> directly.
    /// </summary>
    Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Mailer/IMailerAudience.cs
git commit -m "feat(mailer): define IMailerAudience primitive"
```

---

### Task 9: Add audience DTOs

**Files:**
- Create: `src/Humans.Application/Interfaces/Mailer/Dtos/AudienceStats.cs`
- Create: `src/Humans.Application/Interfaces/Mailer/Dtos/AudienceSyncResult.cs`

- [ ] **Step 1: Write `AudienceStats`**

Create `src/Humans.Application/Interfaces/Mailer/Dtos/AudienceStats.cs`:
```csharp
using NodaTime;

namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>Read-only stats for one audience, shown on the Mailer admin dashboard.</summary>
public sealed record AudienceStats(
    string Key,
    string DisplayName,
    string MailerLiteGroupName,
    int Candidates,
    int ExcludedUnsubscribed,
    int CurrentlyInGroup,
    Instant? LastSyncAt,
    string? LastSyncSummary);
```

- [ ] **Step 2: Write `AudienceSyncResult`**

Create `src/Humans.Application/Interfaces/Mailer/Dtos/AudienceSyncResult.cs`:
```csharp
namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>Post-sync counts for one audience. Mirrors the audit metadata.</summary>
public sealed record AudienceSyncResult(
    string Key,
    string GroupId,
    string GroupName,
    int Candidates,
    int ExcludedUnsubscribed,
    int Created,
    int Assigned,
    int AlreadyAssigned,
    int Unassigned,
    int Errors)
{
    public string FormatSummary() =>
        $"{DisplayCount(Created, "created")}, {DisplayCount(Assigned, "newly assigned")}, " +
        $"{DisplayCount(Unassigned, "unassigned")}, {DisplayCount(Errors, "errors")}.";

    private static string DisplayCount(int n, string label) => $"{n} {label}";
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/Mailer/Dtos/AudienceStats.cs src/Humans.Application/Interfaces/Mailer/Dtos/AudienceSyncResult.cs
git commit -m "feat(mailer): add AudienceStats and AudienceSyncResult DTOs"
```

---

### Task 10: Add the audience architecture pin

**Files:**
- Modify: `tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs`

- [ ] **Step 1: Add two new test methods**

Inside the existing `MailerArchitectureTests` class, add:
```csharp
[HumansFact]
public void AllAudiences_UseHumansPrefix()
{
    var audienceType = typeof(IMailerAudience);
    var impls = typeof(MailerImportService).Assembly
        .GetTypes()
        .Where(t => audienceType.IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
        .ToList();

    impls.Should().NotBeEmpty("at least one IMailerAudience implementation is expected once Phase 3 lands.");

    foreach (var impl in impls)
    {
        var instance = (IMailerAudience)Activator.CreateInstance(impl, NonPublicConstructorBypass(impl))!;
        instance.MailerLiteGroupName.Should().StartWith("Humans - ",
            $"every IMailerAudience must target a Humans-prefixed group; {impl.Name} does not.");
    }
}

[HumansFact]
public void AllAudiences_HaveUniqueGroupNamesAndKeys()
{
    var audienceType = typeof(IMailerAudience);
    var impls = typeof(MailerImportService).Assembly
        .GetTypes()
        .Where(t => audienceType.IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
        .Select(t => (IMailerAudience)Activator.CreateInstance(t, NonPublicConstructorBypass(t))!)
        .ToList();

    impls.Select(a => a.Key).Distinct().Count().Should().Be(impls.Count,
        "audience keys collide");
    impls.Select(a => a.MailerLiteGroupName).Distinct().Count().Should().Be(impls.Count,
        "audience group names collide");
}

// Reflection helper — passes null/default args to allow constructing audiences
// that take service dependencies. The arch test only inspects metadata properties.
private static object?[] NonPublicConstructorBypass(Type t)
{
    var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
    return ctor.GetParameters().Select(p =>
        p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
}
```

- [ ] **Step 2: Run — will fail with `Should().NotBeEmpty()` until Phase 3 adds the first audience**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~AllAudiences" -v quiet`
Expected: FAIL — no audience implementations exist yet.

- [ ] **Step 3: Commit the failing pin**

```bash
git add tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs
git commit -m "test(mailer): pin audience prefix + uniqueness invariants"
```

> The two new tests stay red until Task 13 lands `TicketNoShiftsAudience`. That is expected and intentional — they're the lock-in for Phase 3.

---

## Phase 3: First audience — `TicketNoShiftsAudience`

### Task 11: Add `GetActiveCommittedUserIdsForEventAsync` to Shifts

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs`
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftSignupRepository.cs`
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Shifts/ShiftSignupRepository.cs`
- Test: `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceTests.cs` (or new file if a separate one exists for narrow reads — discover during impl)

- [ ] **Step 1: Locate the existing interface**

Run: `grep -n "interface IShiftSignupService" src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs`
Expected: one match.

- [ ] **Step 2: Add the interface method**

Inside `IShiftSignupService`, add:
```csharp
/// <summary>
/// Returns user-ids with at least one ShiftSignup for the given event whose
/// Status is Pending or Confirmed. Used by audience computations to identify
/// "users who have a shift". Refused/Bailed/Cancelled/NoShow signups do not count.
/// </summary>
Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
    Guid eventSettingsId, CancellationToken ct = default);
```

- [ ] **Step 3: Add the repository method**

Inside `IShiftSignupRepository`, add:
```csharp
Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
    Guid eventSettingsId, CancellationToken ct = default);
```

- [ ] **Step 4: Write the failing service test**

Add to the existing service tests file (or create `ShiftSignupServiceActiveCommittedTests.cs` if a new file is more idiomatic to this codebase):
```csharp
[HumansFact]
public async Task GetActiveCommittedUserIdsForEventAsync_DelegatesToRepository()
{
    var userA = Guid.NewGuid();
    var userB = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var repo = new Mock<IShiftSignupRepository>();
    repo.Setup(r => r.GetActiveCommittedUserIdsForEventAsync(eventId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new HashSet<Guid> { userA, userB });

    var svc = NewService(repo.Object);

    var result = await svc.GetActiveCommittedUserIdsForEventAsync(eventId, CancellationToken.None);

    result.Should().BeEquivalentTo(new[] { userA, userB });
}
```

(`NewService` is the existing test factory in this fixture — match its shape; if there isn't one, build a constructor call with the minimum required dependencies and Mock the rest.)

- [ ] **Step 5: Run — expect failure (method doesn't exist on service)**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~GetActiveCommittedUserIdsForEventAsync_DelegatesToRepository" -v quiet`
Expected: compile error.

- [ ] **Step 6: Implement the service method**

In `ShiftSignupService`:
```csharp
public Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
    Guid eventSettingsId, CancellationToken ct = default) =>
    _repo.GetActiveCommittedUserIdsForEventAsync(eventSettingsId, ct);
```

- [ ] **Step 7: Implement the repository method**

In `ShiftSignupRepository`:
```csharp
public async Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
    Guid eventSettingsId, CancellationToken ct = default)
{
    var committedStatuses = new[] { ShiftSignupStatus.Pending, ShiftSignupStatus.Confirmed };
    var userIds = await _db.ShiftSignups
        .AsNoTracking()
        .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId
                 && committedStatuses.Contains(s.Status))
        .Select(s => s.UserId)
        .Distinct()
        .ToListAsync(ct);
    return userIds.ToHashSet();
}
```

> Verify the navigation path `Shift.Rota.EventSettingsId` exists on this codebase's `ShiftSignup` entity (Shifts.md confirms `ShiftSignup.Shift` and aggregate-local navs to `Rota` and `EventSettings`). If the FK is exposed directly (`Shift.Rota.EventSettingsId` vs an inverse nav), adjust accordingly.

- [ ] **Step 8: Run — test passes**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~GetActiveCommittedUserIdsForEventAsync_DelegatesToRepository" -v quiet`
Expected: PASS.

- [ ] **Step 9: Run all Shifts tests for regression**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~Shifts" -v quiet`
Expected: all PASS (no existing tests should be affected by the additive read).

- [ ] **Step 10: Commit**

```bash
git add src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs \
        src/Humans.Application/Interfaces/Shifts/IShiftSignupRepository.cs \
        src/Humans.Application/Services/Shifts/ShiftSignupService.cs \
        src/Humans.Infrastructure/Repositories/Shifts/ShiftSignupRepository.cs \
        tests/Humans.Application.Tests/Services/Shifts/
git commit -m "feat(shifts): GetActiveCommittedUserIdsForEventAsync narrow read"
```

---

### Task 12: Confirm ticket-side read

**Files:**
- Read-only: `src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs`

- [ ] **Step 1: Inspect existing method signature**

Run: `grep -n "GetAllMatchedUserIdsAsync\|MatchedAttendee" src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs src/Humans.Application/Services/Tickets/TicketQueryService.cs`

Expected: `GetAllMatchedUserIdsAsync` exists. Inspect its implementation: does it return *attendee*-matched users only (Valid|CheckedIn), or also buyer-matched? Per `Tickets.md`: "GetAllMatchedUserIdsAsync (the union of matched attendee user-ids and matched order user-ids)". So it includes buyers.

- [ ] **Step 2: Decide path**

`TicketNoShiftsAudience` needs **attendee-matched only, status Valid|CheckedIn**. Buyer-only matches are excluded per the audience definition.

Two options:
- **(a)** Add a new narrow method `GetUserIdsWithMatchedValidAttendeesAsync(CancellationToken)` on `ITicketQueryService` that returns exactly the attendee-matched set with `Status ∈ {Valid, CheckedIn}` in the active vendor event.
- **(b)** Reuse `GetUserIdsWithTicketsAsync` if such a method already exists with this semantic.

Run: `grep -n "UserIdsWithTickets\|ValidAttendee\|MatchedAttendee" src/Humans.Application/Interfaces/Tickets src/Humans.Application/Services/Tickets`

Expected: discover any existing read with the exact semantic. The `UserIdsWithTickets` cache key referenced in `Tickets.md` may already expose this — look at `TicketQueryService` for a method backing it.

- [ ] **Step 3: Pick the reuse path if available; otherwise add a new method**

If a method like `GetUserIdsWithTicketsAsync()` already returns the correct set (Valid|CheckedIn attendee matches in active event, no buyer-only), reuse it directly. Skip Step 4.

If not, add to `ITicketQueryService`:
```csharp
/// <summary>
/// Returns user-ids with at least one Valid or CheckedIn matched TicketAttendee
/// in the active vendor event. Buyer-only matches excluded.
/// </summary>
Task<IReadOnlySet<Guid>> GetUserIdsWithMatchedValidAttendeesAsync(CancellationToken ct = default);
```

And implement it in `TicketQueryService` by adapting the existing `UserIdsWithTickets` computation (cache key `CacheKeys.UserIdsWithTickets`).

- [ ] **Step 4: If a new method was added, write a test**

Add to the existing `TicketQueryServiceTests`:
```csharp
[HumansFact]
public async Task GetUserIdsWithMatchedValidAttendeesAsync_ExcludesBuyerOnlyAndVoidedAttendees()
{
    // Arrange: seed three users
    //   - userA has a Valid matched attendee → included
    //   - userB has a Void matched attendee → excluded
    //   - userC is a matched buyer with no matched attendees → excluded
    // ...
    // Act
    var result = await _service.GetUserIdsWithMatchedValidAttendeesAsync(CancellationToken.None);
    // Assert
    result.Should().Equal(new HashSet<Guid> { userA });
}
```

Match the existing Tickets test scaffolding (in-memory DbContext seeding pattern used by `TicketQueryServiceTests`).

- [ ] **Step 5: Run + commit**

If you added a new method:
```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~Tickets" -v quiet
git add src/Humans.Application/Interfaces/Tickets/ITicketQueryService.cs \
        src/Humans.Application/Services/Tickets/TicketQueryService.cs \
        tests/Humans.Application.Tests/Services/Tickets/
git commit -m "feat(tickets): GetUserIdsWithMatchedValidAttendeesAsync narrow read"
```

If you reused an existing method, no commit for this task — note in the next task's commit which method was reused.

---

### Task 13: Implement `TicketNoShiftsAudience`

**Files:**
- Create: `src/Humans.Application/Services/Mailer/Audiences/TicketNoShiftsAudience.cs`
- Create: `tests/Humans.Application.Tests/Services/Mailer/Audiences/TicketNoShiftsAudienceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Humans.Application.Tests/Services/Mailer/Audiences/TicketNoShiftsAudienceTests.cs`:
```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Services.Mailer.Audiences;
using Humans.Domain.Entities;
using Moq;

namespace Humans.Application.Tests.Services.Mailer.Audiences;

public class TicketNoShiftsAudienceTests
{
    private static readonly Guid EventId = Guid.NewGuid();

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_ReturnsTicketHoldersMinusShiftHavers()
    {
        var userA = Guid.NewGuid(); // ticket, no shift          → IN
        var userB = Guid.NewGuid(); // ticket, has Confirmed shift → OUT
        var userC = Guid.NewGuid(); // no ticket                  → OUT
        var userD = Guid.NewGuid(); // ticket, has Pending shift   → OUT

        var audience = NewAudience(
            ticketHolders: new HashSet<Guid> { userA, userB, userD },
            shiftCommitted: new HashSet<Guid> { userB, userD });

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEquivalentTo(new[] { userA });
    }

    [HumansFact]
    public async Task ComputeMemberUserIdsAsync_NoActiveEvent_ReturnsEmpty()
    {
        var audience = NewAudience(ticketHolders: new HashSet<Guid> { Guid.NewGuid() },
            shiftCommitted: new HashSet<Guid>(),
            activeEvent: null);

        var members = await audience.ComputeMemberUserIdsAsync(CancellationToken.None);

        members.Should().BeEmpty();
    }

    [HumansFact]
    public void Metadata_UsesHumansPrefix()
    {
        var audience = NewAudience(new HashSet<Guid>(), new HashSet<Guid>());
        audience.Key.Should().Be("ticket-no-shifts");
        audience.MailerLiteGroupName.Should().Be("Humans - Ticket no Shifts");
    }

    private static TicketNoShiftsAudience NewAudience(
        HashSet<Guid> ticketHolders,
        HashSet<Guid> shiftCommitted,
        EventSettings? activeEvent = null)
    {
        var tickets = new Mock<ITicketQueryService>();
        tickets.Setup(t => t.GetUserIdsWithMatchedValidAttendeesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ticketHolders);

        var signups = new Mock<IShiftSignupService>();
        signups.Setup(s => s.GetActiveCommittedUserIdsForEventAsync(EventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(shiftCommitted);

        var mgmt = new Mock<IShiftManagementService>();
        mgmt.Setup(m => m.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeEvent ?? FakeEventSettings(EventId));

        return new TicketNoShiftsAudience(tickets.Object, signups.Object, mgmt.Object);
    }

    private static EventSettings FakeEventSettings(Guid id)
    {
        // Match the construction pattern used in existing Shifts unit tests.
        // If EventSettings has a private constructor, use the existing test factory.
        // Substitute appropriately during implementation.
        return new EventSettings { Id = id, IsActive = true, Year = 2026, Name = "Test" };
    }
}
```

> Implementer note: if `EventSettings` is internal-constructor or has invariants requiring a factory, locate the existing test factory under `tests/Humans.Application.Tests/Services/Shifts/` and reuse. The audience tests don't need a fully-constructed `EventSettings` — only its `Id` is read.

- [ ] **Step 2: Run — should fail to compile**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~TicketNoShiftsAudience" -v quiet`
Expected: compile error — `TicketNoShiftsAudience` doesn't exist yet.

- [ ] **Step 3: Implement the audience**

Create `src/Humans.Application/Services/Mailer/Audiences/TicketNoShiftsAudience.cs`:
```csharp
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;

namespace Humans.Application.Services.Mailer.Audiences;

/// <summary>
/// "Humans - Ticket no Shifts" — humans with a Valid/CheckedIn matched
/// ticket attendee in the active vendor event who have NOT signed up for any
/// shift in the active EventSettings event (Pending and Confirmed signups
/// count as "has a shift"; Refused/Bailed/Cancelled/NoShow do not).
/// </summary>
public sealed class TicketNoShiftsAudience(
    ITicketQueryService tickets,
    IShiftSignupService shiftSignups,
    IShiftManagementService shiftManagement) : IMailerAudience
{
    public string Key => "ticket-no-shifts";
    public string DisplayName => "Ticket holders without a shift";
    public string MailerLiteGroupName => "Humans - Ticket no Shifts";

    public async Task<IReadOnlySet<Guid>> ComputeMemberUserIdsAsync(CancellationToken ct)
    {
        var activeEvent = await shiftManagement.GetActiveAsync(ct);
        if (activeEvent is null) return new HashSet<Guid>();

        var ticketHolders = await tickets.GetUserIdsWithMatchedValidAttendeesAsync(ct);
        var shiftHavers = await shiftSignups.GetActiveCommittedUserIdsForEventAsync(activeEvent.Id, ct);

        var audience = new HashSet<Guid>(ticketHolders);
        audience.ExceptWith(shiftHavers);
        return audience;
    }
}
```

> Implementer note: if Task 12 reused an existing method instead of adding `GetUserIdsWithMatchedValidAttendeesAsync`, swap that call to whichever method name you settled on.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~TicketNoShiftsAudience" -v quiet`
Expected: all PASS.

- [ ] **Step 5: Confirm the architecture pin now passes**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~AllAudiences" -v quiet`
Expected: both `AllAudiences_UseHumansPrefix` and `AllAudiences_HaveUniqueGroupNamesAndKeys` PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Mailer/Audiences/TicketNoShiftsAudience.cs \
        tests/Humans.Application.Tests/Services/Mailer/Audiences/
git commit -m "feat(mailer): add TicketNoShiftsAudience"
```

---

## Phase 4: Audience sync orchestrator

### Task 14: Define `IMailerAudienceSyncService`

**Files:**
- Create: `src/Humans.Application/Interfaces/Mailer/IMailerAudienceSyncService.cs`

- [ ] **Step 1: Write the interface**

```csharp
using Humans.Application.Interfaces.Mailer.Dtos;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// Orchestrates pulling audience definitions, diffing against ML state, and
/// pushing membership changes to MailerLite. Stat-only reads are split out so
/// the dashboard can render without forcing a sync.
/// </summary>
public interface IMailerAudienceSyncService : IApplicationService
{
    /// <summary>Read-only: candidates / excluded-unsubscribed / currently-in-group / last sync.</summary>
    Task<AudienceStats> ComputeStatsAsync(IMailerAudience audience, CancellationToken ct = default);

    /// <summary>Build diff and apply to MailerLite. Writes the summary audit entry.</summary>
    Task<AudienceSyncResult> SyncAsync(IMailerAudience audience, CancellationToken ct = default);

    /// <summary>Calls SyncAsync sequentially for every registered audience.</summary>
    Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Mailer/IMailerAudienceSyncService.cs
git commit -m "feat(mailer): declare IMailerAudienceSyncService"
```

---

### Task 15: Write failing sync orchestrator tests

**Files:**
- Create: `tests/Humans.Application.Tests/Services/Mailer/MailerAudienceSyncServiceTests.cs`

- [ ] **Step 1: Write the test fixture**

Create `tests/Humans.Application.Tests/Services/Mailer/MailerAudienceSyncServiceTests.cs`:
```csharp
using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Mailer;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodaTime;

namespace Humans.Application.Tests.Services.Mailer;

public class MailerAudienceSyncServiceTests
{
    private readonly Mock<IMailerLiteService> _ml = new();
    private readonly Mock<IUserEmailService> _emails = new();
    private readonly Mock<IAuditLogService> _audit = new();

    [HumansFact]
    public async Task SyncAsync_NewUserNotInML_BulkImportsAndAssigns()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", new[] { userA });
        SetupEmails(new (Guid, string)[] { (userA, "a@example.com") });
        SetupGroups(new[] { new MailerLiteGroup("g1", "Humans - A",
            Instant.FromUtc(2026,1,1,0,0), 0, 0, 0, 0, 0) });
        SetupSubscribers(Array.Empty<MailerLiteSubscriber>());
        _ml.Setup(m => m.BulkImportSubscribersToGroupAsync(
                "g1", It.Is<IReadOnlyList<string>>(l => l.Single() == "a@example.com"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkImportResult(1, 0, 0, 0));

        var result = await NewService().SyncAsync(audience.Object, CancellationToken.None);

        result.Created.Should().Be(1);
        result.Assigned.Should().Be(0);
        result.Unassigned.Should().Be(0);
        _ml.Verify(m => m.BulkImportSubscribersToGroupAsync(
            "g1", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [HumansFact]
    public async Task SyncAsync_UnsubscribedUser_ExcludedFromGroup()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", new[] { userA });
        SetupEmails(new (Guid, string)[] { (userA, "a@example.com") });
        SetupGroups(new[] { Group("g1", "Humans - A") });
        SetupSubscribers(new[] { Subscriber("s1", "a@example.com", "unsubscribed") });

        var result = await NewService().SyncAsync(audience.Object, CancellationToken.None);

        result.ExcludedUnsubscribed.Should().Be(1);
        result.Created.Should().Be(0);
        result.Assigned.Should().Be(0);
        _ml.Verify(m => m.AssignSubscriberToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _ml.Verify(m => m.BulkImportSubscribersToGroupAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [HumansFact]
    public async Task SyncAsync_ExistingSubscriberNotInGroup_AssignsIt()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", new[] { userA });
        SetupEmails(new (Guid, string)[] { (userA, "a@example.com") });
        SetupGroups(new[] { Group("g1", "Humans - A") });
        SetupSubscribers(new[] { Subscriber("s1", "a@example.com", "active") });

        var result = await NewService().SyncAsync(audience.Object, CancellationToken.None);

        result.Assigned.Should().Be(1);
        _ml.Verify(m => m.AssignSubscriberToGroupAsync("s1", "g1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [HumansFact]
    public async Task SyncAsync_UserDroppedOut_Unassigned()
    {
        var audience = NewAudience("a-aud", "Humans - A", Array.Empty<Guid>());
        SetupEmails(Array.Empty<(Guid, string)>());
        SetupGroups(new[] { Group("g1", "Humans - A") });
        SetupSubscribers(new[] {
            Subscriber("s1", "a@example.com", "active", inGroups: new[] { "g1" }),
        });

        var result = await NewService().SyncAsync(audience.Object, CancellationToken.None);

        result.Unassigned.Should().Be(1);
        _ml.Verify(m => m.UnassignSubscriberFromGroupAsync("s1", "g1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [HumansFact]
    public async Task SyncAsync_GroupMissing_CreatesItFirst()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", new[] { userA });
        SetupEmails(new (Guid, string)[] { (userA, "a@example.com") });
        SetupGroups(Array.Empty<MailerLiteGroup>());
        _ml.Setup(m => m.CreateGroupAsync("Humans - A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Group("g1", "Humans - A"));
        SetupSubscribers(Array.Empty<MailerLiteSubscriber>());
        _ml.Setup(m => m.BulkImportSubscribersToGroupAsync(
                "g1", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkImportResult(1, 0, 0, 0));

        await NewService().SyncAsync(audience.Object, CancellationToken.None);

        _ml.Verify(m => m.CreateGroupAsync("Humans - A", It.IsAny<CancellationToken>()), Times.Once);
    }

    [HumansFact]
    public async Task SyncAsync_Idempotent_AllAlreadyAssignedOnSecondRun()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", new[] { userA });
        SetupEmails(new (Guid, string)[] { (userA, "a@example.com") });
        SetupGroups(new[] { Group("g1", "Humans - A") });
        SetupSubscribers(new[] {
            Subscriber("s1", "a@example.com", "active", inGroups: new[] { "g1" }),
        });

        var result = await NewService().SyncAsync(audience.Object, CancellationToken.None);

        result.AlreadyAssigned.Should().Be(1);
        result.Created.Should().Be(0);
        result.Assigned.Should().Be(0);
        result.Unassigned.Should().Be(0);
        _ml.VerifyNoOtherCalls();
    }

    [HumansFact]
    public async Task SyncAsync_AssignFails_CountedInErrorsAndSyncContinues()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", new[] { userA, userB });
        SetupEmails(new (Guid, string)[] { (userA, "a@example.com"), (userB, "b@example.com") });
        SetupGroups(new[] { Group("g1", "Humans - A") });
        SetupSubscribers(new[] {
            Subscriber("s1", "a@example.com", "active"),
            Subscriber("s2", "b@example.com", "active"),
        });
        _ml.Setup(m => m.AssignSubscriberToGroupAsync("s1", "g1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("simulated 500"));

        var result = await NewService().SyncAsync(audience.Object, CancellationToken.None);

        result.Errors.Should().Be(1);
        result.Assigned.Should().Be(1); // s2 still succeeded
    }

    [HumansFact]
    public async Task SyncAsync_GroupNameLacksPrefix_ThrowsBeforeAnyMlCall()
    {
        var audience = NewAudience("a-aud", "Newsletter", Array.Empty<Guid>());

        var act = async () => await NewService().SyncAsync(audience.Object, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Humans - *");
        _ml.VerifyNoOtherCalls();
    }

    [HumansFact]
    public async Task SyncAsync_WritesAuditEntryWithCounts()
    {
        var userA = Guid.NewGuid();
        var audience = NewAudience("a-aud", "Humans - A", new[] { userA });
        SetupEmails(new (Guid, string)[] { (userA, "a@example.com") });
        SetupGroups(new[] { Group("g1", "Humans - A") });
        SetupSubscribers(new[] { Subscriber("s1", "a@example.com", "active") });

        await NewService().SyncAsync(audience.Object, CancellationToken.None);

        _audit.Verify(a => a.LogAsync(
            AuditAction.MailerLiteAudienceSyncCompleted,
            null, null, null,
            It.Is<IReadOnlyDictionary<string, object?>>(d =>
                (string)d["audience_key"]! == "a-aud"
                && (int)d["assigned"]! == 1),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- helpers --

    private MailerAudienceSyncService NewService() => new(
        _ml.Object, _emails.Object, _audit.Object,
        NullLogger<MailerAudienceSyncService>.Instance);

    private static Mock<IMailerAudience> NewAudience(
        string key, string groupName, IEnumerable<Guid> members)
    {
        var mock = new Mock<IMailerAudience>();
        mock.SetupGet(a => a.Key).Returns(key);
        mock.SetupGet(a => a.DisplayName).Returns(key);
        mock.SetupGet(a => a.MailerLiteGroupName).Returns(groupName);
        mock.Setup(a => a.ComputeMemberUserIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(members.ToHashSet());
        return mock;
    }

    private void SetupEmails(IEnumerable<(Guid UserId, string Email)> mapping)
    {
        var dict = mapping.ToDictionary(x => x.UserId, x => x.Email);
        _emails.Setup(e => e.GetPrimaryEmailsByUserIdsAsync(
                It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dict);
    }

    private void SetupGroups(IReadOnlyList<MailerLiteGroup> groups)
        => _ml.Setup(m => m.ListGroupsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(groups);

    private void SetupSubscribers(IEnumerable<MailerLiteSubscriber> subscribers)
    {
        async IAsyncEnumerable<MailerLiteSubscriber> Enumerate()
        {
            foreach (var s in subscribers) yield return s;
            await Task.CompletedTask;
        }
        _ml.Setup(m => m.ListSubscribersAsync(It.IsAny<CancellationToken>()))
            .Returns(Enumerate());
    }

    private static MailerLiteGroup Group(string id, string name) =>
        new(id, name, Instant.FromUtc(2026,1,1,0,0), 0, 0, 0, 0, 0);

    private static MailerLiteSubscriber Subscriber(
        string id, string email, string status, string[]? inGroups = null) =>
        new(id, email, status, "api",
            SubscribedAt: Instant.FromUtc(2026,1,1,0,0),
            UnsubscribedAt: null, OptedInAt: null,
            FirstName: null, LastName: null);
        // Note: MailerLiteSubscriber today does not carry a Groups list — see
        // implementation note in Task 16 for how the orchestrator obtains current
        // group membership. The test plumbs that via a separate setup hook.
}
```

> Implementer note: today's `MailerLiteSubscriber` record (Task 9 of Slice 1) does not include a `Groups` collection. The orchestrator's "is this subscriber currently in the target group?" check needs a source — either extend `MailerLiteSubscriber` with `Groups: IReadOnlyList<string>` and parse it from the ML payload's `groups` array, OR fetch group-member-ids via a dedicated `ListGroupSubscriberIdsAsync(groupId)` method on `IMailerLiteService`. The latter keeps the subscriber DTO unchanged but adds a fifth read method. Implementer to pick — recommendation: extend the subscriber DTO (cleaner — group membership is a property of the subscriber per ML's data model).

If extending the DTO is chosen, the `Subscriber()` test helper above needs an extra constructor arg for `Groups`; the `inGroups` parameter is wired to populate it.

- [ ] **Step 2: Run — fail to compile**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~MailerAudienceSyncServiceTests" -v quiet`
Expected: compile error — `MailerAudienceSyncService` doesn't exist.

- [ ] **Step 3: Commit the failing test fixture**

```bash
git add tests/Humans.Application.Tests/Services/Mailer/MailerAudienceSyncServiceTests.cs
git commit -m "test(mailer): write failing MailerAudienceSyncService tests"
```

---

### Task 16: Resolve the "current group membership" decision and add the bulk email-lookup

**Files (decision-dependent):**
- Modify: `src/Humans.Application/Interfaces/Mailer/Dtos/MailerLiteSubscriber.cs` (if extending subscriber DTO)
- Modify: `src/Humans.Infrastructure/Services/Mailer/MailerLiteSubscriberConverter.cs` (if extending subscriber DTO)
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs` (add bulk lookup)
- Modify: `src/Humans.Application/Services/Profiles/UserEmailService.cs` (implement)

- [ ] **Step 1: Extend `MailerLiteSubscriber` with `Groups`**

Edit `src/Humans.Application/Interfaces/Mailer/Dtos/MailerLiteSubscriber.cs` to add the new field:
```csharp
using NodaTime;

namespace Humans.Application.Interfaces.Mailer.Dtos;

public sealed record MailerLiteSubscriber(
    string Id,
    string Email,
    string Status,
    string Source,
    Instant? SubscribedAt,
    Instant? UnsubscribedAt,
    Instant? OptedInAt,
    string? FirstName,
    string? LastName,
    IReadOnlyList<string> GroupIds);  // NEW — IDs of groups this subscriber currently belongs to
```

- [ ] **Step 2: Parse `groups` from the ML payload**

Open `src/Humans.Infrastructure/Services/Mailer/MailerLiteSubscriberConverter.cs` and locate where the converter constructs `MailerLiteSubscriber`. The ML payload's subscriber object includes a `groups` array of objects with `id` and `name`. Extract the ids:
```csharp
// inside the converter's Read method, before constructing the record:
var groupIds = new List<string>();
if (root.TryGetProperty("groups", out var groupsEl) && groupsEl.ValueKind == JsonValueKind.Array)
{
    foreach (var g in groupsEl.EnumerateArray())
    {
        if (g.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            groupIds.Add(idEl.GetString()!);
    }
}

return new MailerLiteSubscriber(
    /* existing args */,
    GroupIds: groupIds);
```

Update existing test fixtures that construct `MailerLiteSubscriber` to pass `Array.Empty<string>()` for the new field.

- [ ] **Step 3: Add `GetPrimaryEmailsByUserIdsAsync` to `IUserEmailService`**

Locate `IUserEmailService` and add:
```csharp
/// <summary>
/// Bulk variant of <see cref="GetPrimaryEmailAsync"/>. Returns a map of user-id
/// to primary email for each user that has one. Users without a primary email
/// are omitted from the result.
/// </summary>
Task<IReadOnlyDictionary<Guid, string>> GetPrimaryEmailsByUserIdsAsync(
    IEnumerable<Guid> userIds, CancellationToken ct = default);
```

Implement in `UserEmailService`. The existing repository likely has the rows already addressable; if not, add a repository method `GetPrimaryEmailRowsForUsersAsync(IEnumerable<Guid>)` and use it.

- [ ] **Step 4: Run existing Mailer + Profiles tests**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~Mailer|FullyQualifiedName~UserEmail" -v quiet`
Expected: all PASS (including the existing import-side tests that read `MailerLiteSubscriber` — they need the `GroupIds` arg updated in their fixture construction).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/Mailer/Dtos/MailerLiteSubscriber.cs \
        src/Humans.Infrastructure/Services/Mailer/MailerLiteSubscriberConverter.cs \
        src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs \
        src/Humans.Application/Services/Profiles/UserEmailService.cs \
        tests/Humans.Application.Tests/Services/Mailer/
git commit -m "feat: MailerLiteSubscriber.GroupIds + bulk primary-email lookup"
```

---

### Task 17: Implement `MailerAudienceSyncService`

**Files:**
- Create: `src/Humans.Application/Services/Mailer/MailerAudienceSyncService.cs`

- [ ] **Step 1: Write the implementation**

```csharp
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Mailer;

public sealed class MailerAudienceSyncService(
    IMailerLiteService ml,
    IUserEmailService emails,
    IAuditLogService audit,
    ILogger<MailerAudienceSyncService> logger,
    IEnumerable<IMailerAudience> audiences) : IMailerAudienceSyncService
{
    private const string HumansGroupPrefix = "Humans - ";
    private static readonly HashSet<string> UnsubscribedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "unsubscribed", "bounced", "junk" };

    public async Task<IReadOnlyList<AudienceSyncResult>> SyncAllAsync(CancellationToken ct = default)
    {
        var results = new List<AudienceSyncResult>();
        foreach (var audience in audiences)
        {
            try
            {
                results.Add(await SyncAsync(audience, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audience sync failed for {Audience}", audience.Key);
            }
        }
        return results;
    }

    public async Task<AudienceStats> ComputeStatsAsync(IMailerAudience audience, CancellationToken ct = default)
    {
        var memberUserIds = await audience.ComputeMemberUserIdsAsync(ct);
        var userEmailMap = await emails.GetPrimaryEmailsByUserIdsAsync(memberUserIds, ct);

        var subscribers = new List<MailerLiteSubscriber>();
        await foreach (var s in ml.ListSubscribersAsync(ct)) subscribers.Add(s);
        var byEmail = subscribers.ToDictionary(s => NormalizeEmail(s.Email), s => s, StringComparer.Ordinal);

        var groups = await ml.ListGroupsAsync(ct);
        var group = groups.FirstOrDefault(g => g.Name == audience.MailerLiteGroupName);

        int excluded = 0, inGroup = 0;
        foreach (var (_, email) in userEmailMap)
        {
            if (!byEmail.TryGetValue(NormalizeEmail(email), out var sub)) continue;
            if (UnsubscribedStatuses.Contains(sub.Status)) excluded++;
            else if (group is not null && sub.GroupIds.Contains(group.Id)) inGroup++;
        }

        return new AudienceStats(
            audience.Key,
            audience.DisplayName,
            audience.MailerLiteGroupName,
            Candidates: userEmailMap.Count,
            ExcludedUnsubscribed: excluded,
            CurrentlyInGroup: inGroup,
            LastSyncAt: null,
            LastSyncSummary: null);
        // LastSyncAt/Summary are populated by the controller layer from the
        // most recent MailerLiteAudienceSyncCompleted audit entry. Keeping the
        // service free of audit-read I/O keeps it focused.
    }

    public async Task<AudienceSyncResult> SyncAsync(IMailerAudience audience, CancellationToken ct = default)
    {
        if (!audience.MailerLiteGroupName.StartsWith(HumansGroupPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Audience '{audience.Key}' targets group '{audience.MailerLiteGroupName}' " +
                $"which does not start with '{HumansGroupPrefix}'.");

        var memberUserIds = await audience.ComputeMemberUserIdsAsync(ct);
        var userEmailMap = await emails.GetPrimaryEmailsByUserIdsAsync(memberUserIds, ct);
        var droppedNoEmail = memberUserIds.Count - userEmailMap.Count;
        if (droppedNoEmail > 0)
            logger.LogInformation(
                "Audience {Audience}: dropped {Count} candidates with no primary email",
                audience.Key, droppedNoEmail);

        // Ensure group exists.
        var groups = await ml.ListGroupsAsync(ct);
        var group = groups.FirstOrDefault(g => g.Name == audience.MailerLiteGroupName)
            ?? await ml.CreateGroupAsync(audience.MailerLiteGroupName, ct);

        // Snapshot ML state.
        var subscribers = new List<MailerLiteSubscriber>();
        await foreach (var s in ml.ListSubscribersAsync(ct)) subscribers.Add(s);
        var byEmail = subscribers.ToDictionary(s => NormalizeEmail(s.Email), s => s, StringComparer.Ordinal);
        var currentGroupMemberIds = subscribers
            .Where(s => s.GroupIds.Contains(group.Id))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Classify candidates.
        var toBulkImport = new List<string>();
        var toAssign = new List<string>();
        var keepSubscriberIds = new HashSet<string>(StringComparer.Ordinal);
        int excluded = 0, alreadyAssigned = 0;

        foreach (var (userId, email) in userEmailMap)
        {
            var norm = NormalizeEmail(email);
            if (!byEmail.TryGetValue(norm, out var sub))
            {
                toBulkImport.Add(email);
                continue;
            }
            if (UnsubscribedStatuses.Contains(sub.Status))
            {
                excluded++;
                continue;
            }
            keepSubscriberIds.Add(sub.Id);
            if (currentGroupMemberIds.Contains(sub.Id))
                alreadyAssigned++;
            else
                toAssign.Add(sub.Id);
        }

        // Compute removals.
        var toUnassign = currentGroupMemberIds.Except(keepSubscriberIds).ToList();

        // Apply.
        int created = 0, assigned = 0, unassigned = 0, errors = 0;

        if (toBulkImport.Count > 0)
        {
            try
            {
                var bulk = await ml.BulkImportSubscribersToGroupAsync(group.Id, toBulkImport, ct);
                created = bulk.Created;
                errors += bulk.Errors;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BulkImport failed for {Audience}", audience.Key);
                errors += toBulkImport.Count;
            }
        }

        foreach (var subId in toAssign)
        {
            try
            {
                await ml.AssignSubscriberToGroupAsync(subId, group.Id, ct);
                assigned++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Assign failed for {Sub} in {Audience}", subId, audience.Key);
                errors++;
            }
        }

        foreach (var subId in toUnassign)
        {
            try
            {
                await ml.UnassignSubscriberFromGroupAsync(subId, group.Id, ct);
                unassigned++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unassign failed for {Sub} in {Audience}", subId, audience.Key);
                errors++;
            }
        }

        var result = new AudienceSyncResult(
            audience.Key, group.Id, group.Name,
            Candidates: userEmailMap.Count,
            ExcludedUnsubscribed: excluded,
            Created: created,
            Assigned: assigned,
            AlreadyAssigned: alreadyAssigned,
            Unassigned: unassigned,
            Errors: errors);

        await audit.LogAsync(
            AuditAction.MailerLiteAudienceSyncCompleted,
            entityType: null, entityId: null, actorUserId: null,
            metadata: new Dictionary<string, object?>
            {
                ["audience_key"] = audience.Key,
                ["group_id"] = group.Id,
                ["group_name"] = group.Name,
                ["candidates"] = result.Candidates,
                ["excluded_unsubscribed"] = result.ExcludedUnsubscribed,
                ["created"] = result.Created,
                ["assigned"] = result.Assigned,
                ["already_assigned"] = result.AlreadyAssigned,
                ["unassigned"] = result.Unassigned,
                ["errors"] = result.Errors,
            },
            description: null,
            ct: ct);

        return result;
    }

    private static string NormalizeEmail(string email) =>
        email.Trim().ToLowerInvariant();
        // If a NormalizingEmailComparer/Normalizer helper exists in Humans.Application
        // (the Tickets section uses one — see Tickets.md), reuse it here instead.
}
```

> Implementer note: confirm the exact signature of `IAuditLogService.LogAsync` — the snippet uses a hypothetical metadata-bag overload. If the existing audit signature differs (`LogAsync(AuditAction, string description, ...)` etc.), serialize the metadata dictionary into a JSON-encoded description string instead. The audit metadata schema (the keys) is the contract; how it's stored is implementation detail.

- [ ] **Step 2: Run tests**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~MailerAudienceSyncServiceTests" -v quiet`
Expected: all PASS.

- [ ] **Step 3: Add architecture pin for the service**

In `MailerArchitectureTests`, add:
```csharp
[HumansFact]
public void MailerAudienceSyncService_LivesInApplication_NoEF()
{
    var serviceType = typeof(MailerAudienceSyncService);
    serviceType.Namespace.Should().Be("Humans.Application.Services.Mailer");
    serviceType.Assembly.GetReferencedAssemblies()
        .Should().NotContain(a => a.Name == "Microsoft.EntityFrameworkCore");
}
```

- [ ] **Step 4: Run all Mailer arch + service tests**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~Mailer" -v quiet`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Mailer/MailerAudienceSyncService.cs \
        tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs
git commit -m "feat(mailer): implement MailerAudienceSyncService"
```

---

## Phase 5: UI + Hangfire job

### Task 18: Extend dashboard view model

**Files:**
- Modify: `src/Humans.Web/Models/Mailer/MailerDashboardViewModel.cs`
- Create: `src/Humans.Web/Models/Mailer/AudienceCardRow.cs`

- [ ] **Step 1: Define `AudienceCardRow`**

Create `src/Humans.Web/Models/Mailer/AudienceCardRow.cs`:
```csharp
using NodaTime;

namespace Humans.Web.Models.Mailer;

public sealed record AudienceCardRow(
    string Key,
    string DisplayName,
    string MailerLiteGroupName,
    int Candidates,
    int ExcludedUnsubscribed,
    int CurrentlyInGroup,
    Instant? LastSyncAt,
    string? LastSyncSummary);
```

- [ ] **Step 2: Extend the dashboard VM**

Edit `MailerDashboardViewModel.cs` — add a new positional field to the record:
```csharp
public sealed record MailerDashboardViewModel(
    MailerLiteAccountSummary? MlSummary,
    IReadOnlyList<MailerLiteGroup>? Groups,
    int HumansMailerLiteContacts,
    int HumansMarketingOptedIn,
    int HumansMarketingOptedOut,
    Instant? LastReconciliationAt,
    string? LastReconciliationSummary,
    DriftReport? Drift,
    string? MlError,
    Instant? CacheFetchedAt,
    IReadOnlyList<AudienceCardRow> Audiences);   // NEW
```

- [ ] **Step 3: Build — controller will fail until we update it**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build FAILS in `MailerAdminController` because the VM constructor now has an extra param.

- [ ] **Step 4: Commit the model changes alone**

```bash
git add src/Humans.Web/Models/Mailer/AudienceCardRow.cs \
        src/Humans.Web/Models/Mailer/MailerDashboardViewModel.cs
git commit -m "feat(mailer): add Audiences field to MailerDashboardViewModel"
```

> Intentionally broken at HEAD — the next task fixes the controller. This is a single PR so the broken-at-this-commit window is short.

---

### Task 19: Wire the controller — `Index` + `Sync` action

**Files:**
- Modify: `src/Humans.Web/Controllers/Mailer/MailerAdminController.cs`

- [ ] **Step 1: Inject `IMailerAudienceSyncService` and audiences enumerable**

Add to the constructor signature and `_field` assignments:
```csharp
private readonly IMailerAudienceSyncService _audienceSync;
private readonly IReadOnlyList<IMailerAudience> _audiences;

public MailerAdminController(
    // ...existing deps...
    IMailerAudienceSyncService audienceSync,
    IEnumerable<IMailerAudience> audiences,
    UserManager<User> userManager)
    : base(userManager)
{
    // ...existing assignments...
    _audienceSync = audienceSync;
    _audiences = audiences.ToList();
}
```

- [ ] **Step 2: Populate `Audiences` in `Index`**

At the bottom of the existing `Index` method, before constructing the VM:
```csharp
var audienceRows = new List<AudienceCardRow>();
foreach (var audience in _audiences)
{
    AudienceStats stats;
    try
    {
        stats = await _audienceSync.ComputeStatsAsync(audience, ct);
    }
    catch (HttpRequestException ex)
    {
        _logger.LogWarning(ex, "Audience stats failed for {Audience}", audience.Key);
        continue; // skip the row rather than fail the whole dashboard
    }
    var lastSync = await _audit.GetFilteredEntriesAsync(
        actions: new[] { AuditAction.MailerLiteAudienceSyncCompleted },
        limit: 50, // we filter by audience_key in-memory
        ct: ct);
    var lastForThisAudience = lastSync.FirstOrDefault(e =>
        TryGetMetadata(e, "audience_key") == audience.Key);
    audienceRows.Add(new AudienceCardRow(
        audience.Key, audience.DisplayName, audience.MailerLiteGroupName,
        stats.Candidates, stats.ExcludedUnsubscribed, stats.CurrentlyInGroup,
        lastForThisAudience?.OccurredAt,
        lastForThisAudience?.Description));
}
```

(`TryGetMetadata` is a small helper that pulls a key from the audit entry's metadata bag — match whatever shape the existing audit entries expose.)

Update the VM construction to pass `audienceRows` as the last argument.

- [ ] **Step 3: Add the `Sync` action**

Add to the controller:
```csharp
[HttpPost("Audiences/{key}/Sync")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SyncAudience(string key, CancellationToken ct)
{
    var audience = _audiences.FirstOrDefault(a => a.Key == key);
    if (audience is null) return NotFound();

    try
    {
        var result = await _audienceSync.SyncAsync(audience, ct);
        TempData["Banner"] = $"{audience.DisplayName}: {result.FormatSummary()}";
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Audience sync failed for {Audience}", key);
        TempData["Banner"] = $"{audience.DisplayName}: sync failed — {ex.Message}";
    }
    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/Mailer/MailerAdminController.cs
git commit -m "feat(mailer): wire Audiences card and Sync action in MailerAdminController"
```

---

### Task 20: Render the dashboard card

**Files:**
- Create: `src/Humans.Web/Views/Mailer/Admin/_AudiencesCard.cshtml`
- Modify: `src/Humans.Web/Views/Mailer/Admin/Index.cshtml`

- [ ] **Step 1: Write the partial**

Create `src/Humans.Web/Views/Mailer/Admin/_AudiencesCard.cshtml`:
```cshtml
@model IReadOnlyList<Humans.Web.Models.Mailer.AudienceCardRow>

<section class="card mb-4">
    <header class="card-header">
        <h2 class="h5 mb-0">Audiences</h2>
    </header>
    <div class="card-body">
        @if (Model.Count == 0)
        {
            <p class="text-muted mb-0">No audiences registered.</p>
        }
        else
        {
            <ul class="list-unstyled mb-0">
                @foreach (var row in Model)
                {
                    <li class="d-flex justify-content-between align-items-center py-2 border-bottom">
                        <div>
                            <strong>@row.MailerLiteGroupName</strong>
                            <div class="small text-muted">
                                @row.Candidates humans match · @row.ExcludedUnsubscribed ML-unsubscribed ·
                                @row.CurrentlyInGroup currently in group
                                @if (row.LastSyncAt is not null)
                                {
                                    <text> · last sync @row.LastSyncAt.Value.ToDateTimeUtc().ToString("yyyy-MM-dd HH:mm 'UTC'")</text>
                                }
                            </div>
                        </div>
                        <form asp-action="SyncAudience" asp-route-key="@row.Key" method="post" class="m-0">
                            <button type="submit" class="btn btn-sm btn-outline-primary">Push Now</button>
                        </form>
                    </li>
                }
            </ul>
        }
    </div>
</section>
```

- [ ] **Step 2: Render it from `Index.cshtml`**

Open `src/Humans.Web/Views/Mailer/Admin/Index.cshtml` and add the partial render at the appropriate visual location (after the existing dashboard summary, before the drift report — matches the spec's UI ordering):
```cshtml
<partial name="_AudiencesCard" model="Model.Audiences" />
```

- [ ] **Step 3: Manual smoke (optional but useful)**

Run: `dotnet run --project src/Humans.Web` and navigate to `/Mailer/Admin` as admin. Visually verify the card renders with the one row "Humans - Ticket no Shifts". Stop the server.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Mailer/Admin/_AudiencesCard.cshtml \
        src/Humans.Web/Views/Mailer/Admin/Index.cshtml
git commit -m "feat(mailer): render Audiences card on /Mailer/Admin"
```

---

### Task 21: Controller tests

**Files:**
- Create: `tests/Humans.Web.Tests/Controllers/Mailer/MailerAdminControllerAudienceSyncTests.cs`

- [ ] **Step 1: Write the failing tests**

Match the shape of the existing `MailerAdminControllerTests.cs` for setup helpers. Outline:
```csharp
public class MailerAdminControllerAudienceSyncTests
{
    [HumansFact]
    public async Task SyncAudience_AsAdmin_ReturnsRedirectWithBanner()
    {
        var audience = StubAudience("ticket-no-shifts");
        var sync = new Mock<IMailerAudienceSyncService>();
        sync.Setup(s => s.SyncAsync(It.IsAny<IMailerAudience>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudienceSyncResult(
                "ticket-no-shifts", "g1", "Humans - Ticket no Shifts",
                Candidates: 10, ExcludedUnsubscribed: 1,
                Created: 5, Assigned: 3, AlreadyAssigned: 1, Unassigned: 0, Errors: 0));

        var controller = NewController(audiences: new[] { audience }, sync: sync.Object);
        var result = await controller.SyncAudience("ticket-no-shifts", CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be(nameof(MailerAdminController.Index));
        controller.TempData["Banner"].Should().NotBeNull();
    }

    [HumansFact]
    public async Task SyncAudience_UnknownKey_Returns404()
    {
        var controller = NewController(audiences: Array.Empty<IMailerAudience>(),
            sync: Mock.Of<IMailerAudienceSyncService>());
        var result = await controller.SyncAudience("nope", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // helpers omitted — match existing fixture pattern
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~MailerAdminControllerAudienceSyncTests" -v quiet`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Web.Tests/Controllers/Mailer/MailerAdminControllerAudienceSyncTests.cs
git commit -m "test(mailer): controller smoke for SyncAudience action"
```

---

### Task 22: Hangfire recurring job

**Files:**
- Create: `src/Humans.Infrastructure/Jobs/MailerAudienceSyncJob.cs`
- Modify: `src/Humans.Web/Extensions/RecurringJobExtensions.cs`

- [ ] **Step 1: Write the job**

Create `src/Humans.Infrastructure/Jobs/MailerAudienceSyncJob.cs`:
```csharp
using Hangfire;
using Humans.Application.Interfaces.Mailer;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
public sealed class MailerAudienceSyncJob
{
    private readonly IMailerAudienceSyncService _sync;
    private readonly ILogger<MailerAudienceSyncJob> _logger;

    public MailerAudienceSyncJob(IMailerAudienceSyncService sync, ILogger<MailerAudienceSyncJob> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("MailerAudienceSyncJob starting");
        var results = await _sync.SyncAllAsync(ct);
        _logger.LogInformation(
            "MailerAudienceSyncJob completed: {Count} audiences processed",
            results.Count);
    }
}
```

- [ ] **Step 2: Register in `RecurringJobExtensions`**

Open `src/Humans.Web/Extensions/RecurringJobExtensions.cs`. Inside `UseHumansRecurringJobs`, near the top after the existing `ticketSyncInterval` resolution, add:
```csharp
var mailerAudienceCron = app.Configuration.GetValue<string>("MailerLite:AudienceSyncCron")
    ?? "0 6 * * *";
```

Then add to the `jobs` array (anywhere logical — placing it after the Ticketing entries makes sense):
```csharp
("mailer-audience-sync", () => RecurringJob.AddOrUpdate<MailerAudienceSyncJob>(
    "mailer-audience-sync", job => job.ExecuteAsync(CancellationToken.None), mailerAudienceCron)),
```

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Jobs/MailerAudienceSyncJob.cs \
        src/Humans.Web/Extensions/RecurringJobExtensions.cs
git commit -m "feat(mailer): register MailerAudienceSyncJob (daily 06:00 UTC by default)"
```

---

### Task 23: DI registration

**Files:**
- Modify: wherever Slice 1 registered Mailer services (`Program.cs` or a Mailer-specific extension method — discover via grep)

- [ ] **Step 1: Locate the Mailer DI block**

Run: `grep -rn "IMailerImportService\|IMailerLiteService" src/Humans.Web/Program.cs src/Humans.Web/Extensions/`
Expected: one block where Slice 1 wired things up.

- [ ] **Step 2: Add the new registrations**

In the same block:
```csharp
services.AddScoped<IMailerAudienceSyncService, MailerAudienceSyncService>();
services.AddSingleton<IMailerAudience, TicketNoShiftsAudience>();
services.AddTransient<MailerAudienceSyncJob>();
```

> Lifetime note: `TicketNoShiftsAudience` is a singleton, but its constructor takes scoped services (`ITicketQueryService`, etc.). In ASP.NET Core, registering a singleton that depends on scoped services would throw at first resolution. Verify: either register the audience as `Transient`/`Scoped` to match its deps, or refactor the audience to use `IServiceScopeFactory` and resolve its deps per call. Recommended path: register as `Scoped` (fast enough; one audience instance per request/job execution).
>
> Change the registration to:
> ```csharp
> services.AddScoped<IMailerAudience, TicketNoShiftsAudience>();
> ```

- [ ] **Step 3: Build + run full test suite**

Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet`
Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/
git commit -m "feat(mailer): DI registration for audience sync service + audience + job"
```

---

## Phase 6: Docs

### Task 24: Update `docs/sections/Mailer.md`

**Files:**
- Modify: `docs/sections/Mailer.md`

- [ ] **Step 1: Update the invariants block**

Replace the existing two lines about `IMailerLiteService` GET-only with the three replacement lines from §6 of the spec (`docs/superpowers/specs/2026-05-14-mailer-outbound-audiences-design.md`).

- [ ] **Step 2: Remove the "inbound-only" line**

Delete the line:
```
- Inbound-only is a known compliance gap. **Outbound is the next slice and must ship before any other Mailer feature.** Drift in the Humans-newer-than-ML direction is mitigated only by the dashboard drift report (§6.1 of the spec) and manual admin remediation in the ML UI until outbound lands.
```

- [ ] **Step 3: Add to *Cross-Section Dependencies***

Append:
```markdown
- **Tickets:** `ITicketQueryService.GetUserIdsWithMatchedValidAttendeesAsync` — audience-side ticket-holder enumeration for `TicketNoShiftsAudience`.
- **Shifts:** `IShiftSignupService.GetActiveCommittedUserIdsForEventAsync` and `IShiftManagementService.GetActiveAsync` — audience-side shift commitment + active-event lookup.
```

(If Task 12 reused an existing method, update the Tickets line to reference it.)

- [ ] **Step 4: Add to *Routing***

Append to the routing list:
```markdown
- `/Mailer/Admin/Audiences/{key}/Sync` — on-demand audience push (POST)
```

- [ ] **Step 5: Add a *Concepts* entry**

Add to the Concepts section:
```markdown
- **Audience** — a code-defined `IMailerAudience` implementation whose `MailerLiteGroupName` starts with `"Humans - "`. Membership is computed from Humans state and synced into the ML group by `MailerAudienceSyncService` (daily Hangfire job + on-demand admin button).
```

- [ ] **Step 6: Commit**

```bash
git add docs/sections/Mailer.md
git commit -m "docs(mailer): relax inbound-only; document audience framework"
```

---

### Task 25: Update `docs/sections/AuditLog.md`

**Files:**
- Modify: `docs/sections/AuditLog.md`

- [ ] **Step 1: List the new action**

Run: `grep -n MailerLiteReconciliationCompleted docs/sections/AuditLog.md`
Expected: one match (if `AuditLog.md` enumerates actions).

Immediately after that entry, add a row for `MailerLiteAudienceSyncCompleted` matching the surrounding table/list format.

- [ ] **Step 2: Commit**

```bash
git add docs/sections/AuditLog.md
git commit -m "docs(audit): list MailerLiteAudienceSyncCompleted action"
```

---

## Phase 7: End-to-end verification

### Task 26: Final build, test, and smoke

- [ ] **Step 1: Full build + test**

Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet`
Expected: build succeeds; all tests pass.

- [ ] **Step 2: Architecture pins**

Run: `dotnet test Humans.slnx --filter "FullyQualifiedName~MailerArchitectureTests" -v quiet`
Expected: all four pins pass:
- `IMailerLiteService_OnlyAllowsAudienceWrites`
- `AllAudiences_UseHumansPrefix`
- `AllAudiences_HaveUniqueGroupNamesAndKeys`
- `MailerAudienceSyncService_LivesInApplication_NoEF`

- [ ] **Step 3: Manual smoke**

Run: `dotnet run --project src/Humans.Web`. Log in as admin. Visit `/Mailer/Admin`. Verify the Audiences card appears with one row. Click **Push Now**.

Expected:
- 302 redirect back to `/Mailer/Admin` with a banner "Ticket holders without a shift: 0 created, N newly assigned, …" or similar.
- Audit entry `MailerLiteAudienceSyncCompleted` appears in `/AuditLog`.
- Refresh `/Mailer/Admin` — `currently in group` count should now equal `candidates − ML-unsubscribed`.

Stop the server.

- [ ] **Step 4: Push the branch**

```bash
git push origin mailer-outbound-audiences
```

- [ ] **Step 5: Open the PR**

```bash
gh pr create --base main --head mailer-outbound-audiences --title "Mailer: outbound slice + audience framework + Ticket-no-Shifts" --body "$(cat <<'EOF'
## Summary
- Adds four prefix-guarded outbound writes to `IMailerLiteService` (`CreateGroupAsync`, `AssignSubscriberToGroupAsync`, `UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`). All writes runtime-rejected unless the target group's name starts with `"Humans - "`.
- Introduces `IMailerAudience` code-registered audience primitive and `MailerAudienceSyncService` orchestrator (compute → diff → apply → audit).
- Ships first audience `TicketNoShiftsAudience` → `"Humans - Ticket no Shifts"` ML group.
- Daily Hangfire job (`mailer-audience-sync`, default 06:00 UTC, configurable via `MailerLite:AudienceSyncCron`) + on-demand admin button on `/Mailer/Admin`.
- Relaxes `docs/sections/Mailer.md` inbound-only invariant; pins new invariants in architecture tests.

Spec: [`docs/superpowers/specs/2026-05-14-mailer-outbound-audiences-design.md`](docs/superpowers/specs/2026-05-14-mailer-outbound-audiences-design.md).

## Test plan
- [ ] Architecture pins pass (`MailerArchitectureTests` — four pins)
- [ ] Sync-service unit tests pass (idempotency, unsubscribed exclusion, group auto-create, error counting, prefix violation, audit metadata)
- [ ] Audience unit tests pass (Valid/CheckedIn ticket × Pending/Confirmed/non-active shift matrix)
- [ ] Controller test passes (admin redirect with banner; 404 on unknown key)
- [ ] Manual: `/Mailer/Admin` Push Now produces a 302 + audit entry; second click is no-op (all-zero deltas)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-review checklist

After completing all tasks, before requesting code review:

- [ ] Every spec requirement maps to a task. Cross-reference against §3, §4, §7, §8, §9 of the spec.
- [ ] No `TBD`, `TODO`, or "implement later" left in code.
- [ ] Every new method has at least one unit test.
- [ ] Every architecture invariant claimed in `docs/sections/Mailer.md` is pinned by a test.
- [ ] Hangfire job name `mailer-audience-sync` is consistent everywhere.
- [ ] DI lifetimes match dependency lifetimes (`IMailerAudience` registered Scoped because its deps are Scoped).
- [ ] Cache invalidation runs after every write in `MailerLiteClient` so the dashboard reflects post-sync state on the next render.
