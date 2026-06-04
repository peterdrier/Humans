# Survey Section — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a first-party, GDPR-compliant **Survey** vertical section: author typed/branching multi-language surveys, send tokenised email invites + one reminder to a resolved audience, collect responses across three anonymity tiers, and read results via an in-app view, CSV/JSON export, and a key-authed read-only analysis API.

**Architecture:** New vertical section `Survey` born §15-compliant — `Humans.Application.Services.Survey.SurveyService` (no EF) over `ISurveyRepository` (`[Section("Survey")]`, owns the six `survey_*` tables), `SurveyAdminController`/`SurveyController`/`SurveysApiController` in Web, `SendSurveyReminderJob` in Infrastructure. All cross-section calls go through existing `I…ServiceRead` interfaces; cross-domain FKs are FK-only with `[Obsolete]` navs the repo never `.Include()`s. No caching decorator in v1.

**Tech Stack:** ASP.NET Core MVC, EF Core (Npgsql/Postgres, jsonb), NodaTime (`Instant`/`IClock`), Hangfire (recurring job), ASP.NET Data Protection (invite tokens), xUnit + FluentAssertions, Roslyn architecture analyzers (HUM0017/HUM0025).

**Spec:** `docs/superpowers/specs/2026-06-03-survey-section-design.md` (decisions resolved 2026-06-04, §15).

---

## Deviations from the spec — decided from the codebase audit (review these first)

These refine the spec where the audit found the assumed mechanism absent or unsuitable. Each is the more reuse-correct path; flagged here for Peter's plan review.

1. **Authorization policy.** Spec §9 says `AdminOrBoard`; **no such policy exists**. Use `PolicyNames.BoardOrAdmin` (`RequireRole(RoleNames.Board, RoleNames.Admin)`). Register no new policy.
2. **Audience.** **Locked (business):** the send model is an **idempotent per-recipient invitation ledger** — each "send" resolves a target set, diffs against existing invitations, and creates+emails only the net-new people (send {A,B} then {B,C,D} ⇒ only C,D invited; nobody double-invited; sends never revoke). That behaviour lives in `SendInvitesAsync` (Task 3.4) and is the part that matters. **OPEN (business, settle before Phase 3):** which **target predicates** v1 offers (all-members / team / ticket-holders, possibly the same cohorts Mailer defines) and whether a survey invite honours the marketing opt-out. **Deferred (tech):** where predicates are computed / whether a cross-section read interface is added — *not* decided now. The `SurveyAudienceType`/`AudienceSelection` shapes in this plan are a **provisional placeholder** for the locked send model, not a final targeting decision.
3. **Email ↔ invitation linkage.** To avoid coupling the horizontal Email section to Survey, do **not** add a `SurveyInvitationId` FK to the shared `EmailOutboxMessage`/`EmailMessage`. Stamp `SurveyInvitation.LatestEmailStatus = Queued` at enqueue and `Failed` on a synchronous send exception — mirroring what `CampaignService` itself stamps. The outbox→grant *final-Sent* callback is **not** reproduced in v1 (acceptable at 500-user scale; revisit only if delivery-status accuracy is needed).
4. **Caching.** Per spec §12, **none**. Register `SurveyService` as a plain Scoped service (Feedback/Issues pattern), not the Camps caching-decorator pattern.
5. **`LocalizedText`.** A Survey-/Domain-owned value object wrapping `Dictionary<string,string>` (culture→text), persisted as jsonb via the `DocumentVersionConfiguration` precedent (`HasColumnType("jsonb")` + `HasConversion` + `ValueComparer`). Lives in `Humans.Domain/ValueObjects/` so entities can use it; promotion to a broader shared primitive stays a later refactor (§15.1).
6. **`Cultures` column dropped (derived); `PublicSlug` is IN v1.** Spec §3 listed `Survey.Cultures string[]` — dropped; "which cultures have content" is **derived** from the `LocalizedText` dictionaries (no separate column to drift). **`PublicSlug` stays** (the public answering path is in v1 per the 2026-06-04 decision), plus `PublicStartedCount` for the slug-side funnel. Public responses are always Anonymous, `InputMethod=Slug`.
7. **Membership gate.** The answering wizard controller (`SurveyController`) must be reachable by invited non-members → add `"Survey"` to `MembershipRequiredFilter.ExemptControllers` and mark the answer actions `[AllowAnonymous]` (mirrors `Guest`/`Camp`).
10. **Completion is a boolean, not a timestamp (privacy — Peter).** A completion-tracked response is "done but anonymous." Storing *when* the invitee completed (`Invitation.CompletedAt`) leaks: that user-linked time matches the unattributed response's `SubmittedAt` and re-identifies them. So `SurveyInvitation` records `Completed` as a **bool** with **no time column and no `UpdatedAt`**, and individual response submissions are **not** audit-logged. Identified completions are likewise just `Completed=true` (their `Response` is legitimately linked and carries `SubmittedAt` if a time is ever needed).

9. **No `[Obsolete]` navs, no cross-section FK constraints — corrects the spec AND the `Issue`/`Feedback` cow-path.** The spec §3 (and the `Issue`/`FeedbackReport`/`Camp` code) keep `[Obsolete]`-marked cross-domain navigation properties "stitched in memory." A new section must **not** be born with `[Obsolete]` anything — that is the exact debt the hard rules flag. Survey references Users/Teams by **bare `Guid` FK columns only**: no navigation property, no cross-section EF FK constraint (the clean `FeedbackReport.AgentConversationId` precedent). The service resolves cross-section display data (names, `PreferredLanguage`) via `IUserServiceRead`/`ITeamServiceRead` and stitches it into **DTOs/ViewModels**, never onto entities. design-rules §6c says "FK only" — taken literally. This matches the **clean** sections already in the repo (`Expenses`, `Store` team-orders, `Holded`) and the rule in `memory/architecture/no-cross-section-ef-joins.md`; the `[Obsolete]`-nav style on `Issue`/`Feedback`/`Camp` is the older debt we are **not** propagating.

8. **Wizard state (DECIDED 2026-06-04).** **Identified** in-progress responses are **persisted** (a draft `SurveyResponse`, `SubmittedAt == null`, answers saved per page) and **resume** when the invitee re-clicks their link — found by `(SurveyId, UserId, SubmittedAt is null)`. **CompletionTracked / Anonymous** carry no user/invitation link, so they can't be located on return → held in **session** and written atomically at submit; reopening **restarts** them. Post-submit *editing* of a finished response is out of v1 (resume is for unfinished drafts only). Pairs with Deviation #10 (boolean completion).

---

## Anonymity encoding (load-bearing — used in Phase 4 & 6)

| Tier | `Response.UserId` | `Response.InvitationId` | `Invitation.Completed` | Resume? | Notes |
|------|------|------|------|------|------|
| **Identified** | invitee id | invitation id | **true** | **yes** | only tier that is personal data; only tier in GDPR export, per-respondent drill-down & API identity fields; in-progress draft persisted |
| **CompletionTracked** | `null` | **`null`** | **true (bool only — no time)** | no | participation counted (response-rate, no reminder) but answers unlinkable; **no completion timestamp** |
| **Anonymous** (invited) | `null` | **`null`** | **false (untouched)** | no | no trace; may still get the one reminder (disclosed on choice step) |

`InvitationId`/`UserId` are set **only** for `Identified`. The invitation is known from the wizard token throughout the session, so `Invitation.Completed` is flipped at submit for Identified/CompletionTracked **without** persisting the link on the response. **`Completed` is a boolean with no timestamp** — recording *when* a completion-tracked invitee finished would correlate (user-linked) with the unattributed response's `SubmittedAt` and re-identify them. Resume works only for Identified (the only tier with a findable draft, `SubmittedAt is null`); others are session-only and restart on reopen.

---

## File Structure (what gets created/modified)

**Domain** (`src/Humans.Domain/`)
- `ValueObjects/LocalizedText.cs` — culture→text VO + `Resolve(culture, defaultCulture)`.
- `ValueObjects/BranchCondition.cs` + `BranchClause` — skip-logic predicate.
- `Enums/SurveyStatus.cs`, `Enums/SurveyQuestionType.cs`, `Enums/ResponseAnonymity.cs`, `Enums/BranchCombine.cs`, `Enums/BranchOperator.cs`, `Enums/SurveyAudienceType.cs`.
- `Entities/Survey.cs`, `Entities/SurveyQuestion.cs`, `Entities/SurveyQuestionOption.cs`, `Entities/SurveyInvitation.cs`, `Entities/SurveyResponse.cs`, `Entities/SurveyAnswer.cs`.

**Application** (`src/Humans.Application/`)
- `Interfaces/Repositories/ISurveyRepository.cs` — `[Section("Survey")] partial interface : IRepository`.
- `Interfaces/Survey/ISurveyService.cs` (+ DTOs co-located), `Interfaces/Survey/ISurveyServiceRead.cs`.
- `Services/Survey/SurveyService.cs` (authoring, send, submit, results, export, GDPR contributor).
- `Services/Survey/SurveyBranchingEvaluator.cs` — pure, testable branching + visibility/validation.
- `Services/Survey/SurveyInviteTokenProvider.cs` interface `ISurveyInviteTokenProvider` (impl in Infrastructure).
- Add `GdprExportSections.SurveyResponses` constant.

**Infrastructure** (`src/Humans.Infrastructure/`)
- `Repositories/Survey/SurveyRepository.cs` — `internal sealed partial class : ISurveyRepository`.
- `Data/Configurations/Survey/{Survey,SurveyQuestion,SurveyQuestionOption,SurveyInvitation,SurveyResponse,SurveyAnswer}Configuration.cs`.
- `Data/HumansDbContext.cs` — add six `DbSet`s.
- `Migrations/<timestamp>_AddSurveySection.cs` — generated, **never hand-edited**.
- `Services/Survey/SurveyInviteTokenProvider.cs` — Data-Protection impl.
- `Jobs/SendSurveyReminderJob.cs` — `IRecurringJob`.

**Web** (`src/Humans.Web/`)
- `Controllers/SurveyAdminController.cs` (BoardOrAdmin builder/send/results/export), `Controllers/SurveyController.cs` (`[AllowAnonymous]` wizard), `Controllers/SurveysApiController.cs` (key-authed read API).
- `Filters/ApiKeyAuthFilter.cs` — add `SurveyApiSettings` + `SurveyApiKeyAuthFilter`.
- `Models/SurveyViewModels.cs` (+ `Models/Survey/` for builder sub-VMs as needed).
- `Models/Survey/SurveyResultsBuilder.cs`, `Models/Survey/SurveyCsvExportBuilder.cs`.
- `Views/SurveyAdmin/` (Index, Builder, Send, Results), `Views/Survey/` (wizard: Intro, Page, ThankYou, Closed).
- `Extensions/Sections/SurveySectionExtensions.cs` (+ call in `InfrastructureServiceCollectionExtensions.cs`).
- `Extensions/Infrastructure/ConfigurationMetadataExtensions.cs` — register `SURVEY_API_KEY`.
- `Extensions/RecurringJobExtensions.cs` — register `survey-reminder` cron.
- `Authorization/MembershipRequiredFilter.cs` — add `"Survey"` to `ExemptControllers`.

**Tests** (`tests/`)
- `Humans.Application.Tests/Survey/` — `LocalizedTextTests`, `SurveyBranchingEvaluatorTests`, `SurveyServiceTests` (authoring/send/submit/results/GDPR), `SurveyInviteTokenTests`.
- `Humans.Application.Tests/Architecture/SurveyArchitectureTests.cs` + add Survey to `ServiceBoundaryArchitectureTests.RepositoryOwners`.

**Docs**
- `docs/sections/survey.md` — section invariant doc (per `SECTION-TEMPLATE.md`).

---

## Phases (one branch, one PR — phases organise the work, they are not separate PRs)

- **Phase 0** — Domain model, EF configs, migration, DbSets.
- **Phase 1** — Repository + service authoring CRUD + branching validation + DI + architecture tests.
- **Phase 2** — Admin builder UI.
- **Phase 3** — Audience resolution + invitations + invite/reminder email templates + token.
- **Phase 4** — Answering wizard (branching evaluation, anonymity, submit).
- **Phase 5** — Reminder job.
- **Phase 6** — Results view + CSV/JSON export + analysis API.
- **Phase 7** — GDPR contributor + transparency note + section doc + final green.

Commit after every task. Run `dotnet build Humans.slnx -v quiet` and the relevant `dotnet test Humans.slnx -v quiet` filter before each commit.

---

## Phase 0 — Domain model & schema

### Task 0.1: `LocalizedText` value object (TDD)

**Files:**
- Create: `src/Humans.Domain/ValueObjects/LocalizedText.cs`
- Test: `tests/Humans.Application.Tests/Survey/LocalizedTextTests.cs`

- [ ] **Step 1 — Failing test**
```csharp
namespace Humans.Application.Tests.Survey;

public class LocalizedTextTests
{
    [Fact]
    public void Resolve_prefers_requested_culture()
    {
        var t = new LocalizedText(new Dictionary<string, string> { ["en"] = "Hello", ["es"] = "Hola" });
        t.Resolve("es", "en").Should().Be("Hola");
    }

    [Fact]
    public void Resolve_falls_back_to_default_then_any_present()
    {
        var t = new LocalizedText(new Dictionary<string, string> { ["en"] = "Hello" });
        t.Resolve("de", "en").Should().Be("Hello");           // default-culture fallback
        new LocalizedText(new Dictionary<string, string> { ["it"] = "Ciao" })
            .Resolve("de", "en").Should().Be("Ciao");          // any-present fallback
    }

    [Fact]
    public void Empty_resolves_to_empty_string_and_equality_is_by_value()
    {
        LocalizedText.Empty.Resolve("en", "en").Should().BeEmpty();
        new LocalizedText(new Dictionary<string, string> { ["en"] = "x" })
            .Should().Be(new LocalizedText(new Dictionary<string, string> { ["en"] = "x" }));
    }
}
```
- [ ] **Step 2 — Run, expect FAIL** (`LocalizedText` undefined):
  `dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~LocalizedTextTests`
- [ ] **Step 3 — Implement**
```csharp
namespace Humans.Domain.ValueObjects;

/// <summary>Per-culture authored content (culture code → text). Persisted as a single jsonb column.</summary>
public sealed class LocalizedText : IEquatable<LocalizedText>
{
    private readonly Dictionary<string, string> _values;

    public LocalizedText(IDictionary<string, string> values)
        => _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    public static LocalizedText Empty { get; } = new(new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Requested culture → default culture → any present → "".</summary>
    public string Resolve(string culture, string defaultCulture)
    {
        if (_values.TryGetValue(culture, out var v) && !string.IsNullOrEmpty(v)) return v;
        if (_values.TryGetValue(defaultCulture, out var d) && !string.IsNullOrEmpty(d)) return d;
        foreach (var s in _values.Values) if (!string.IsNullOrEmpty(s)) return s;
        return string.Empty;
    }

    public bool HasCulture(string culture) => _values.TryGetValue(culture, out var v) && !string.IsNullOrEmpty(v);

    public bool Equals(LocalizedText? other) =>
        other is not null && _values.Count == other._values.Count &&
        _values.All(kv => other._values.TryGetValue(kv.Key, out var o) && o == kv.Value);

    public override bool Equals(object? obj) => Equals(obj as LocalizedText);
    public override int GetHashCode() =>
        _values.OrderBy(k => k.Key, StringComparer.Ordinal)
               .Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key.GetHashCode(), kv.Value.GetHashCode()));
}
```
- [ ] **Step 4 — Run, expect PASS.**
- [ ] **Step 5 — Commit:** `feat(survey): add LocalizedText value object`

### Task 0.2: Enums + `BranchCondition`

**Files:** Create the six enum files + `src/Humans.Domain/ValueObjects/BranchCondition.cs`.

- [ ] **Step 1 — Implement enums** (one file each, `namespace Humans.Domain.Enums`):
```csharp
public enum SurveyStatus { Draft = 0, Open = 1, Closed = 2 }
public enum SurveyQuestionType { SingleChoice = 0, MultiChoice = 1, ShortText = 2, LongText = 3, Rating = 4 }
public enum ResponseAnonymity { Identified = 0, CompletionTracked = 1, Anonymous = 2 }
public enum BranchCombine { All = 0, Any = 1 }
public enum BranchOperator { Is = 0, IsNot = 1, Answered = 2, NotAnswered = 3 }
public enum SurveyAudienceType { Team = 0, AllActiveMembers = 1, TicketHolders = 2, ShiftParticipants = 3 }
public enum SurveyInputMethod { UserSpecificLink = 0, Slug = 1 }
```
- [ ] **Step 2 — Implement `BranchCondition`** (`namespace Humans.Domain.ValueObjects`):
```csharp
public sealed class BranchClause
{
    public Guid QuestionId { get; set; }
    public BranchOperator Operator { get; set; }
    public List<string> OptionValues { get; set; } = new();   // stable Option.Value strings
}

public sealed class BranchCondition
{
    public BranchCombine Combine { get; set; } = BranchCombine.All;
    public List<BranchClause> Clauses { get; set; } = new();
}
```
(Plain mutable classes — they are jsonb payloads, mirroring `CampLink`. No behaviour; evaluation lives in `SurveyBranchingEvaluator`, Task 1.x.)
- [ ] **Step 3 — Build:** `dotnet build Humans.slnx -v quiet` → expect success.
- [ ] **Step 4 — Commit:** `feat(survey): add survey enums and BranchCondition`

### Task 0.3: Entities

**Files:** the six `src/Humans.Domain/Entities/Survey*.cs`.

- [ ] **Step 1 — Implement** (`namespace Humans.Domain.Entities`; NodaTime `Instant`; cross-domain refs are bare `Guid` FK columns — **no navs, no `[Obsolete]`**):
```csharp
public class Survey
{
    public Guid Id { get; init; }
    public LocalizedText Title { get; set; } = LocalizedText.Empty;
    public LocalizedText Intro { get; set; } = LocalizedText.Empty;
    public LocalizedText ThankYou { get; set; } = LocalizedText.Empty;
    public string DefaultCulture { get; set; } = "en";
    public bool AllowAnonymous { get; set; }
    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;
    public Instant? OpensAt { get; set; }
    public Instant? ClosesAt { get; set; }
    public SurveyAudienceType? AudienceType { get; set; }
    public Guid? AudienceTeamId { get; set; }                 // bare Guid when AudienceType == Team; no nav, no cross-section FK constraint
    public string? PublicSlug { get; set; }                   // public answering link; requires AllowAnonymous; null = invite-only
    public int PublicStartedCount { get; set; }               // slug-path "started" funnel counter (anonymous → no per-person anchor)
    public Guid CreatedByUserId { get; init; }   // bare FK: no nav, no cross-section EF FK constraint; resolve via IUserServiceRead
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public ICollection<SurveyQuestion> Questions { get; set; } = new List<SurveyQuestion>();
}

public class SurveyQuestion
{
    public Guid Id { get; init; }
    public Guid SurveyId { get; init; }
    public int PageNumber { get; set; }
    public int Order { get; set; }
    public SurveyQuestionType Type { get; set; }
    public LocalizedText Prompt { get; set; } = LocalizedText.Empty;
    public LocalizedText HelpText { get; set; } = LocalizedText.Empty;
    public bool IsRequired { get; set; }
    public int? RatingMin { get; set; }
    public int? RatingMax { get; set; }
    public LocalizedText RatingMinLabel { get; set; } = LocalizedText.Empty;
    public LocalizedText RatingMaxLabel { get; set; } = LocalizedText.Empty;
    public BranchCondition? ShowIf { get; set; }
    public Survey Survey { get; set; } = null!;
    public ICollection<SurveyQuestionOption> Options { get; set; } = new List<SurveyQuestionOption>();
}

public class SurveyQuestionOption
{
    public Guid Id { get; init; }
    public Guid QuestionId { get; init; }
    public int Order { get; set; }
    public string Value { get; set; } = string.Empty;          // stable machine value, not localised
    public LocalizedText Label { get; set; } = LocalizedText.Empty;
    public SurveyQuestion Question { get; set; } = null!;
}

public class SurveyInvitation
{
    public Guid Id { get; init; }
    public Guid SurveyId { get; init; }
    public Guid UserId { get; init; }   // bare FK: no nav, no cross-section EF FK constraint; resolve via IUserServiceRead
    public Instant? SentAt { get; set; }
    public EmailOutboxStatus? LatestEmailStatus { get; set; }
    public Instant? ReminderSentAt { get; set; }
    public bool Completed { get; set; }   // flag only — NO completion timestamp (a precise time would correlate with an anon/completion-tracked response's SubmittedAt and unmask)
    public bool Started { get; set; }     // funnel "started" — set on first advance past intro; bool only, no timestamp
    public Instant CreatedAt { get; init; }
}

public class SurveyResponse
{
    public Guid Id { get; init; }
    public Guid SurveyId { get; init; }
    public Guid? InvitationId { get; init; }                   // set ONLY for Identified
    public Guid? UserId { get; init; }                         // set ONLY for Identified; bare FK, no nav, no cross-section EF FK constraint
    public ResponseAnonymity Anonymity { get; init; }
    public SurveyInputMethod InputMethod { get; init; }        // UserSpecificLink vs Slug — funnel split by entry path
    public string Culture { get; init; } = "en";
    public Instant? SubmittedAt { get; set; }                  // null = in-progress draft (Identified only; resumable §8); set at final submit
    public ICollection<SurveyAnswer> Answers { get; set; } = new List<SurveyAnswer>();
}

public class SurveyAnswer
{
    public Guid Id { get; init; }
    public Guid ResponseId { get; init; }
    public Guid QuestionId { get; init; }
    public List<string> SelectedOptionValues { get; set; } = new();   // jsonb
    public string? TextValue { get; set; }
    public int? RatingValue { get; set; }
    public SurveyResponse Response { get; set; } = null!;
}
```
- [ ] **Step 2 — Build** → expect success (entities import no cross-section types — `User`/`Team` are never referenced; only bare `Guid`s).
- [ ] **Step 3 — Commit:** `feat(survey): add survey domain entities`

### Task 0.4: EF configurations + DbSets

**Files:** six `src/Humans.Infrastructure/Data/Configurations/Survey/*Configuration.cs`; modify `src/Humans.Infrastructure/Data/HumansDbContext.cs`.

- [ ] **Step 1 — `SurveyConfiguration`** (full exemplar; LocalizedText jsonb mirrors `DocumentVersionConfiguration`). Add a shared static `JsonSerializerOptions JsonOptions` and a `LocalizedText` converter helper to avoid repetition:
```csharp
internal static class SurveyJson
{
    public static readonly JsonSerializerOptions Options = new();

    public static void LocalizedText<TEntity>(
        EntityTypeBuilder<TEntity> b,
        System.Linq.Expressions.Expression<Func<TEntity, LocalizedText>> prop) where TEntity : class
        => b.Property(prop)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v.Values, Options),
                v => new LocalizedText(JsonSerializer.Deserialize<Dictionary<string, string>>(v, Options)
                                       ?? new Dictionary<string, string>()),
                new ValueComparer<LocalizedText>(
                    (a, c) => a!.Equals(c),
                    v => v.GetHashCode(),
                    v => v));
}

public class SurveyConfiguration : IEntityTypeConfiguration<Survey>
{
    public void Configure(EntityTypeBuilder<Survey> b)
    {
        b.ToTable("surveys");
        b.HasKey(s => s.Id);
        SurveyJson.LocalizedText(b, s => s.Title);
        SurveyJson.LocalizedText(b, s => s.Intro);
        SurveyJson.LocalizedText(b, s => s.ThankYou);
        b.Property(s => s.DefaultCulture).HasMaxLength(10).IsRequired();
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.AudienceType).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.PublicSlug).HasMaxLength(80);
        b.HasIndex(s => s.Status);
        b.HasIndex(s => s.PublicSlug).IsUnique();   // filtered to non-null — copy an existing filtered-unique index's HasFilter(...) form
        b.HasMany(s => s.Questions).WithOne(q => q.Survey)
            .HasForeignKey(q => q.SurveyId).OnDelete(DeleteBehavior.Cascade);
        // CreatedByUserId / AudienceTeamId are bare Guid columns: NO navigation property and NO
        // cross-section EF FK constraint (FeedbackReport.AgentConversationId precedent). The service
        // resolves the creator's display name via IUserServiceRead only when needed.
    }
}
```
  > **No cross-section navs or FK constraints, no `[Obsolete]` anything.** Survey references Users/Teams by bare `Guid` columns only — the clean `FeedbackReport.AgentConversationId` precedent, not the `[Obsolete]`-nav grandfathered debt on `Issue`/`FeedbackReport`/`Camp`. design-rules §6c says "FK only"; we take that literally for a new section. No `HUM0024` grandfathering — there is nothing to grandfather.
- [ ] **Step 2 — Remaining five configs** (same file-per-entity pattern; key points):
  - `SurveyQuestionConfiguration` → table `survey_questions`; `Prompt`/`HelpText`/`RatingMinLabel`/`RatingMaxLabel` via `SurveyJson.LocalizedText`; `Type` `HasConversion<string>()`; `ShowIf` jsonb (`HasColumnType("jsonb")` + `HasConversion` to/from `BranchCondition?` with `SurveyJson.Options`); `HasMany(Options).WithOne(Question).HasForeignKey(QuestionId).OnDelete(Cascade)`; index `(SurveyId, PageNumber, Order)`.
  - `SurveyQuestionOptionConfiguration` → `survey_question_options`; `Value` `HasMaxLength(100).IsRequired()`; `Label` localized jsonb.
  - `SurveyInvitationConfiguration` → `survey_invitations`; `LatestEmailStatus` `HasConversion<string>().HasMaxLength(20)`; **unique index `(SurveyId, UserId)`**; index `(SurveyId, Completed, SentAt)`. `Completed` is a plain `bool` — **no completion-time column, no `UpdatedAt`** (timing side-channel, §4). `UserId` is a bare `Guid` column — no nav, no cross-section FK constraint.
  - `SurveyResponseConfiguration` → `survey_responses`; `Anonymity` `HasConversion<string>()`; `InputMethod` `HasConversion<string>()`; `Culture` `HasMaxLength(10)`; `HasMany(Answers).WithOne(Response).HasForeignKey(ResponseId).OnDelete(Cascade)`; FK to `SurveyInvitation` via `InvitationId` `OnDelete(SetNull)` (intra-section, fine); `UserId` is a bare `Guid?` column — no nav, no cross-section FK constraint; indexes `SurveyId`, `(SurveyId, UserId)`.
  - `SurveyAnswerConfiguration` → `survey_answers`; `SelectedOptionValues` jsonb (`List<string>`, copy the `ProfileConfiguration` Allergies converter); `TextValue` `HasMaxLength(4000)`; FK to `SurveyQuestion` via `QuestionId` `OnDelete(Restrict)`; indexes `ResponseId`, `QuestionId`.
- [ ] **Step 3 — Add DbSets** to `HumansDbContext`:
```csharp
public DbSet<Survey> Surveys => Set<Survey>();
public DbSet<SurveyQuestion> SurveyQuestions => Set<SurveyQuestion>();
public DbSet<SurveyQuestionOption> SurveyQuestionOptions => Set<SurveyQuestionOption>();
public DbSet<SurveyInvitation> SurveyInvitations => Set<SurveyInvitation>();
public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
public DbSet<SurveyAnswer> SurveyAnswers => Set<SurveyAnswer>();
```
- [ ] **Step 4 — Build** → expect success.
- [ ] **Step 5 — Commit:** `feat(survey): add EF configurations and DbSets`

### Task 0.5: Migration (generated — never hand-edit)

- [ ] **Step 1 — Generate** (Infrastructure is the migrations assembly; use the project's standard invocation):
  `dotnet ef migrations add AddSurveySection --project src/Humans.Infrastructure --startup-project src/Humans.Web`
- [ ] **Step 2 — Inspect** the generated `Up()`: six `create table` for `survey_*`, jsonb columns with `'{}'::jsonb` defaults, the unique `(survey_id, user_id)` invitation index, FKs with the specified delete behaviours. If anything is wrong, **fix the model/configuration and regenerate** (`dotnet ef migrations remove` then re-add) — do **not** edit the migration by hand.
- [ ] **Step 3 — Build** → success.
- [ ] **Step 4 — Commit:** `feat(survey): add AddSurveySection migration`

---

## Phase 1 — Repository, service authoring CRUD, branching validation, DI, architecture tests

### Task 1.1: `ISurveyRepository` + `SurveyRepository` (authoring reads/writes)

**Files:**
- Create: `src/Humans.Application/Interfaces/Repositories/ISurveyRepository.cs`
- Create: `src/Humans.Infrastructure/Repositories/Survey/SurveyRepository.cs`

- [ ] **Step 1 — Interface** (`[Section("Survey")] partial interface ISurveyRepository : IRepository`): methods —
  `Task<Survey?> GetByIdAsync(Guid id, CancellationToken ct)` (Include Questions→Options),
  `Task<IReadOnlyList<Survey>> GetAllSummariesAsync(CancellationToken ct)` (no Include; for the admin index),
  `Task AddAsync(Survey survey, CancellationToken ct)`,
  `Task UpdateAsync(Survey survey, CancellationToken ct)` (full-graph upsert of questions/options),
  `Task<SurveyStatus?> GetStatusAsync(Guid id, CancellationToken ct)`,
  plus invitation/response/results methods added in later tasks (interface is `partial` — extend per phase).
- [ ] **Step 2 — Impl** mirrors `CampRepository`: `internal sealed partial class SurveyRepository(IDbContextFactory<HumansDbContext> factory) : ISurveyRepository`, per-call `await using var ctx = await factory.CreateDbContextAsync(ct)`, `.AsNoTracking()` reads, `.Include(s => s.Questions).ThenInclude(q => q.Options)` for the full graph, tracked load+mutate+`SaveChangesAsync` for writes. There are **no** cross-section navs to `.Include` — creator/respondent display data is resolved by the service via `IUserServiceRead`.
- [ ] **Step 3 — Build** → success.
- [ ] **Step 4 — Commit:** `feat(survey): add ISurveyRepository and SurveyRepository`

### Task 1.2: `SurveyBranchingEvaluator` (TDD — pure logic)

**Files:**
- Create: `src/Humans.Application/Services/Survey/SurveyBranchingEvaluator.cs`
- Test: `tests/Humans.Application.Tests/Survey/SurveyBranchingEvaluatorTests.cs`

This pure component is reused by the wizard (Phase 4) for visibility, required-validation, and rejecting hidden answers; and by author-save validation (Task 1.4) for no-forward-reference checks. Answers-so-far are modelled as `IReadOnlyDictionary<Guid, IReadOnlyList<string>>` (questionId → selected option values; empty/absent = unanswered).

- [ ] **Step 1 — Failing tests** covering: `Is`/`IsNot`/`Answered`/`NotAnswered`; `All` vs `Any`; null `ShowIf` ⇒ visible; a clause referencing an unanswered question ⇒ `Is` false / `NotAnswered` true. Example:
```csharp
public class SurveyBranchingEvaluatorTests
{
    private static BranchCondition Cond(BranchCombine c, params BranchClause[] cl)
        => new() { Combine = c, Clauses = cl.ToList() };
    private static BranchClause Clause(Guid q, BranchOperator op, params string[] vals)
        => new() { QuestionId = q, Operator = op, OptionValues = vals.ToList() };

    [Fact]
    public void Null_condition_is_visible()
        => SurveyBranchingEvaluator.IsVisible(null, new Dictionary<Guid, IReadOnlyList<string>>()).Should().BeTrue();

    [Fact]
    public void Is_matches_selected_value()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, IReadOnlyList<string>> { [q] = new[] { "yes" } };
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "yes")), answers).Should().BeTrue();
        SurveyBranchingEvaluator.IsVisible(Cond(BranchCombine.All, Clause(q, BranchOperator.Is, "no")), answers).Should().BeFalse();
    }

    [Fact]
    public void Any_combine_is_or()
    {
        var q = Guid.NewGuid();
        var answers = new Dictionary<Guid, IReadOnlyList<string>> { [q] = new[] { "a" } };
        SurveyBranchingEvaluator.IsVisible(
            Cond(BranchCombine.Any, Clause(q, BranchOperator.Is, "a"), Clause(q, BranchOperator.Is, "z")), answers)
            .Should().BeTrue();
    }
}
```
- [ ] **Step 2 — Run, expect FAIL.**
- [ ] **Step 3 — Implement** `static class SurveyBranchingEvaluator` with `bool IsVisible(BranchCondition? cond, IReadOnlyDictionary<Guid, IReadOnlyList<string>> answers)` evaluating clauses per operator and combining with All/Any; plus `IReadOnlyList<Guid> ValidateNoForwardReferences(IReadOnlyList<SurveyQuestion> ordered)` returning offending question ids whose `ShowIf` references a question not strictly earlier in `(PageNumber, Order)`.
- [ ] **Step 4 — Run, expect PASS.**
- [ ] **Step 5 — Commit:** `feat(survey): add branching evaluator`

### Task 1.3: `ISurveyService` / `ISurveyServiceRead` + DTOs

**Files:** `src/Humans.Application/Interfaces/Survey/ISurveyService.cs`, `ISurveyServiceRead.cs`.

- [ ] **Step 1 — Read interface** (cross-section surface; minimal): `ISurveyServiceRead` with what other sections might need — for v1, nothing cross-section consumes Survey, so keep it tiny (e.g. `Task<IReadOnlyList<SurveySummary> > GetOpenSurveysAsync(...)` only if a consumer appears; otherwise an empty read interface is acceptable to establish the boundary). `public interface ISurveyService : ISurveyServiceRead, IApplicationService`.
- [ ] **Step 2 — DTOs** co-located in `ISurveyService.cs` (records): `SurveySummary`, `SurveyDetail`, `SurveyEditInput`, `QuestionInput`, `OptionInput`, `AudienceSelection`, `SendResult`, `SurveyResultsView`, `QuestionAggregate`, `RespondentDetail`. Define every field now (used across later tasks) — e.g.:
```csharp
public sealed record SurveySummary(Guid Id, string Title, SurveyStatus Status, int InvitedCount, int ResponseCount);
public sealed record AudienceSelection(SurveyAudienceType Type, Guid? TeamId);
public sealed record SendResult(int InvitationsCreated, int EmailsQueued, int Failed);
```
- [ ] **Step 3 — Service method signatures** on `ISurveyService` (bodies in later tasks): `CreateAsync`, `UpdateAsync` (authoring graph), `GetForEditAsync`, `GetSummariesAsync`, `OpenAsync`/`CloseAsync`, `SendInvitesAsync(Guid surveyId, CancellationToken)`, `PreviewAudienceCountAsync`, `SubmitResponseAsync(SurveySubmission, CancellationToken)`, `GetResultsAsync`, `ExportCsvAsync`, `ExportJsonAsync`, plus the API read methods (Phase 6) and `ContributeForUserAsync` (GDPR, Phase 7).
- [ ] **Step 4 — Build** → success. **Commit:** `feat(survey): add survey service interfaces and DTOs`

### Task 1.4: `SurveyService` authoring (create/update/open/close) (TDD)

**Files:** `src/Humans.Application/Services/Survey/SurveyService.cs`; Test: `tests/Humans.Application.Tests/Survey/SurveyServiceTests.cs` (mock `ISurveyRepository`, `IClock`, `IAuditLogService` with NSubstitute/Moq — match the repo's existing mocking lib; check an existing `*ServiceTests`).

- [ ] **Step 1 — Failing tests:** create persists a Draft with normalized questions/options ordering; `UpdateAsync` rejects a survey whose `ShowIf` forward-references (uses `SurveyBranchingEvaluator.ValidateNoForwardReferences`) by throwing `ValidationException`/returning a result error (match codebase convention); `OpenAsync` flips Draft→Open and stamps `UpdatedAt` from `IClock`.
- [ ] **Step 2 — Run, expect FAIL.**
- [ ] **Step 3 — Implement** `public sealed class SurveyService : ISurveyService` with ctor `(ISurveyRepository repo, IUserServiceRead userService, ITeamServiceRead teamService, ITicketServiceRead ticketService, IEmailService emailService, IEmailMessageFactory emailMessages, ISurveyInviteTokenProvider tokenProvider, IAuditLogService auditLog, IClock clock, ILogger<SurveyService> logger)`. Implement authoring methods; validate branching on save; write audit entries via `IAuditLogService.LogAsync`. (Send/submit/results/export/GDPR added in their phases.)
- [ ] **Step 4 — Run, expect PASS.** **Commit:** `feat(survey): implement survey authoring`

### Task 1.5: DI wiring (no caching decorator)

**Files:** Create `src/Humans.Web/Extensions/Sections/SurveySectionExtensions.cs`; modify `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`.

- [ ] **Step 1 — Implement** (Feedback/Issues pattern — plain Scoped, no decorator):
```csharp
internal static class SurveySectionExtensions
{
    internal static IServiceCollection AddSurveySection(this IServiceCollection services)
    {
        services.AddSingleton<ISurveyRepository, SurveyRepository>();      // IDbContextFactory ⇒ Singleton-safe
        services.AddScoped<SurveyService>();
        services.AddScoped<ISurveyService>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyServiceRead>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<SurveyService>());  // Phase 7
        services.AddScoped<ISurveyInviteTokenProvider, SurveyInviteTokenProvider>();             // Phase 3

        services.Configure<SurveyApiSettings>(o =>                                                // Phase 6
            o.ApiKey = Environment.GetEnvironmentVariable("SURVEY_API_KEY") ?? string.Empty);
        services.AddScoped<SurveyApiKeyAuthFilter>();
        return services;
    }
}
```
- [ ] **Step 2 — Call** `services.AddSurveySection();` in `AddHumansInfrastructure()` alongside the other `Add…Section()` calls.
- [ ] **Step 3 — Build** → success. **Commit:** `feat(survey): wire DI for Survey section`

### Task 1.6: Architecture tests

**Files:** Create `tests/Humans.Application.Tests/Architecture/SurveyArchitectureTests.cs`; modify `tests/Humans.Application.Tests/Architecture/ServiceBoundaryArchitectureTests.cs`.

- [ ] **Step 1 — Add Survey to `RepositoryOwners`** dictionary (`["ISurveyRepository"] = "Survey"`).
- [ ] **Step 2 — `SurveyArchitectureTests`** pinning the `ISurveyRepository` consumer allow-list (only `SurveyService` + the repo impl), mirroring `CampsArchitectureTests.ICampRepository_HasNoUnexpectedConsumers`.
- [ ] **Step 3 — Run** the architecture test group + confirm HUM0017/HUM0025 build-time analyzers are clean (no cross-section repo injection; single owner for each `survey_*` table). `dotnet test Humans.slnx -v quiet --filter FullyQualifiedName~Architecture`
- [ ] **Step 4 — Commit:** `test(survey): architecture tests and ownership map`

---

## Phase 2 — Admin builder UI

`SurveyAdminController` (`[Authorize(Policy = PolicyNames.BoardOrAdmin)]`, `[Route("Survey/Admin")]`), derived from the appropriate `Humans…ControllerBase`. Controllers contain no logic beyond parse→service→format (hard rule).

### Task 2.1: Admin index + create

- [ ] **Files:** `Controllers/SurveyAdminController.cs`, `Models/SurveyViewModels.cs`, `Views/SurveyAdmin/Index.cshtml`, `Views/SurveyAdmin/Builder.cshtml`.
- [ ] **Step 1 — Index action** → `surveyService.GetSummariesAsync()` → `Views/SurveyAdmin/Index.cshtml` table (title, status, response/invited rate, links to Builder/Send/Results). Sorting/filtering done in the controller (hard rule).
- [ ] **Step 2 — Create/Edit** GET renders `Builder` from `GetForEditAsync`; POST binds a `SurveyBuilderViewModel` → `SurveyEditInput` → `CreateAsync`/`UpdateAsync`. The builder edits: title/intro/thankyou per culture (culture switcher over `CultureCatalog.SupportedCultureCodes`), `AllowAnonymous`, `DefaultCulture`, pages/questions/options, per-question `ShowIf` rule builder (choice questions only), `OpensAt`/`ClosesAt`, audience (`SurveyAudienceType` + team picker when `Team`), and **enable public link** (sets `PublicSlug`; requires `AllowAnonymous`; enforces uniqueness and rejects reserved slugs `Admin`/`Answer`).
- [ ] **Step 3 — Build + manual smoke** (deferred to Phase 7 site test). **Commit:** `feat(survey): admin index and builder`

### Task 2.2: Open/Close + branching rule builder UX

- [ ] Open/Close POST actions → `OpenAsync`/`CloseAsync`. Rule builder posts `BranchCondition` JSON per question; server re-validates via the evaluator on save (Task 1.4 already enforces). **Commit:** `feat(survey): open/close and branching builder`

---

## Phase 3 — Audience, invitations, email, token

### Task 3.1: `ISurveyInviteTokenProvider` (TDD round-trip)

**Files:** `src/Humans.Application/Services/Survey/ISurveyInviteTokenProvider.cs` (interface), `src/Humans.Infrastructure/Services/Survey/SurveyInviteTokenProvider.cs`; Test: `tests/Humans.Application.Tests/Survey/SurveyInviteTokenTests.cs`.

- [ ] **Step 1 — Interface:** `string Create(Guid invitationId); Guid? Resolve(string token);`
- [ ] **Step 2 — Failing test** using a test `IDataProtectionProvider` (`DataProtectionProvider.Create("test")` from `Microsoft.AspNetCore.DataProtection`): `Resolve(Create(id)) == id`; tampered token ⇒ `null`. Mirrors `UnsubscribeTokenProvider`/`MagicLinkUrlBuilder`.
- [ ] **Step 3 — Implement:** purpose `"SurveyInvite"`, `ITimeLimitedDataProtector`, payload `invitationId.ToString()`, lifetime e.g. `TimeSpan.FromDays(60)`; `Resolve` returns null on `CryptographicException`. (Hardening: a per-survey lifetime tied to `ClosesAt` is a later refinement — fixed lifetime is fine for v1.)
- [ ] **Step 4 — Run, expect PASS.** **Commit:** `feat(survey): invite token provider`

### Task 3.2: Audience resolution (TDD)

> **v1 predicate menu (decided 2026-06-04):** `Team` (ship first), `AllActiveMembers`, `TicketHolders`, `ShiftParticipants`. The **locked** part is the idempotent diff-and-send in Task 3.4. Surveys are **never marketing** (ticket-holders / members, operational comms) → invites do **not** honour the marketing opt-out; sent as `MessageCategory.System` (always-send).

- [ ] **Step 1 — Failing test** on `SurveyService.PreviewAudienceCountAsync` / internal `ResolveRecipientIdsAsync(AudienceSelection)`: `Team` → `ITeamServiceRead.GetTeamAsync(teamId)` member ids; `AllActiveMembers` → active-member ids from `IUserServiceRead`; `TicketHolders` → `ITicketServiceRead` holder ids; `ShiftParticipants` → the shifts read interface that `HasShiftAudience` uses (exact name confirmed at impl). Mock the read interfaces.
- [ ] **Step 2 — Implement** the resolver as a private method returning `IReadOnlySet<Guid>` from the injected reads (mirror `CampaignService.SendWaveAsync` team resolution). **No** `IMailerAudience` usage (avoids the marketing opt-out filter).
- [ ] **Step 3 — Commit:** `feat(survey): resolve invite audience via read interfaces`

### Task 3.3: Email templates (3-touch)

**Files:** modify `IEmailMessageFactory.cs`, `EmailMessageFactory.cs`, `IEmailRenderer.cs` (+ the renderer impl + resx/templates the renderer uses — follow `TermRenewalReminder`/`ReConsentReminder` end-to-end).

- [ ] **Step 1 — Factory + renderer methods:** `EmailMessage SurveyInvitation(string email, string name, string surveyTitle, string surveyUrl, string? culture)` and `SurveyReminder(...)`; matching `IEmailRenderer.RenderSurveyInvitation/RenderSurveyReminder(...)`. `TemplateName` = `"survey_invitation"` / `"survey_reminder"`; `Category` = `MessageCategory.System` (operational — surveys are never marketing, so always-send is correct; mirrors how magic-link/verification categorise).
- [ ] **Step 2 — Implement renderer bodies** (localised subject/body, link to `surveyUrl`) following the existing renderer's culture-branching.
- [ ] **Step 3 — Build** → success. **Commit:** `feat(survey): invite and reminder email templates`

### Task 3.4: `SendInvitesAsync` wave (TDD)

- [ ] **Step 1 — Failing test:** for a survey with audience `Team`, creates one `SurveyInvitation` per resolved user (idempotent on `(SurveyId, UserId)` — re-send skips existing), stamps `SentAt`+`LatestEmailStatus=Queued`, builds `/Survey/Answer?t={token}` via `ISurveyInviteTokenProvider` + base URL, and calls `IEmailService.SendAsync` once per recipient with the recipient's `PreferredLanguage`. On a thrown send, stamps `LatestEmailStatus=Failed`. Mirrors `CampaignService.SendWaveAsync`.
- [ ] **Step 2 — Repository methods** (extend `ISurveyRepository` partial): `GetInvitedUserIdsAsync(surveyId)`, `AddInvitationAndSaveAsync(invitation)`, `UpdateInvitationStatusAsync(id, status, at)`.
- [ ] **Step 3 — Implement** the loop; resolve names/languages via `IUserServiceRead.GetUserInfosAsync` and emails via `IUserEmailService.GetNotificationTargetEmailsAsync` (as `CampaignService` does). Build absolute URL using the same base-URL source `MagicLinkUrlBuilder` uses.
- [ ] **Step 4 — Run, expect PASS.** **Commit:** `feat(survey): send invitation wave`

### Task 3.5: Send UI

- [ ] `SurveyAdminController` Send GET (preview audience size via `PreviewAudienceCountAsync`) + POST (`SendInvitesAsync`), `Views/SurveyAdmin/Send.cshtml` showing per-invite delivery status. **Commit:** `feat(survey): admin send view`

---

## Phase 4 — Answering wizard

`SurveyController` (`[Route("Survey")]`); add `"Survey"` to `MembershipRequiredFilter.ExemptControllers`; answer actions `[AllowAnonymous]`. In-progress page answers held in **server-side session** keyed by token (per Deviation #8). Response written atomically at submit.

### Task 4.1: Wizard entry + privacy/language step

- [ ] **Step 1 — `GET /Survey/Answer?t={token}`** → `tokenProvider.Resolve(t)` → invitation → survey. If survey `Closed` or past `ClosesAt` → `Views/Survey/Closed.cshtml`. **Resume check:** if an Identified draft exists (`(SurveyId, UserId, SubmittedAt is null)`), jump straight back into it at the first unanswered page. Otherwise render `Views/Survey/Intro.cshtml`: `Survey.Intro` resolved in culture, the **transparency note** (Phase 7 copy), and — when `AllowAnonymous` — the three-choice `ResponseAnonymity` selector with the reminder-tradeoff note; language picker for the anonymous path (default from `CultureInfo.CurrentUICulture`). On choosing **Identified**, create the draft response now; for CompletionTracked/Anonymous keep `{anonymity, culture}` in session only.
- [ ] **Step 2 — Build.** **Commit:** `feat(survey): wizard intro and privacy step`

### Task 4.2: Question pages + server-side branching (TDD on the page-sequencing helper)

- [ ] **Step 1 — Failing test** on a `SurveyWizardFlow` helper: given ordered questions with `ShowIf` and accumulated answers, `NextVisiblePage(currentPage, answers)` skips pages whose every question is hidden; `VisibleQuestionsOnPage(page, answers)` filters by `SurveyBranchingEvaluator.IsVisible`; `RequiredUnanswered(visibleQuestions, answers)` ignores hidden questions.
- [ ] **Step 2 — Implement** the helper (pure, over the Phase-1 evaluator).
- [ ] **Step 3 — Controller** `GET/POST /Survey/Answer/Page` renders one page; on POST records that page's answers into session, validates required-visible, computes next visible page. On the **first** advance past the intro, flip `Invitation.Started = true` (bool, no time) for the funnel.
- [ ] **Step 4 — Commit:** `feat(survey): wizard pages with server-side branching`

### Task 4.3: Submit (TDD — anonymity encoding)

- [ ] **Step 1 — Failing test** on `SurveyService.SubmitResponseAsync(SurveySubmission)`:
  - `Identified` → finalise draft `SurveyResponse` with `UserId`+`InvitationId` set, `Anonymity=Identified`, `SubmittedAt` set; invitation `Completed = true`.
  - `CompletionTracked` → response with `UserId=null`, `InvitationId=null`, `SubmittedAt` set; invitation `Completed = true` (**boolean only — no timestamp written**).
  - `Anonymous` → response with `UserId=null`, `InvitationId=null`, `SubmittedAt` set; invitation `Completed` left **false**.
  - Every submitted response records `InputMethod` (`UserSpecificLink` in v1; `Slug` only if the public path is in scope).
  - Server **rejects** answers to questions that should have been hidden (re-evaluate full branching from the submission; drop/forbid hidden answers).
- [ ] **Step 2 — Repository:** `AddResponseWithAnswersAndSaveAsync(response)` (or finalise the existing Identified draft), `SetInvitationCompletedAsync(invitationId)` (flips the bool — **no time argument**).
- [ ] **Step 3 — Implement** `SubmitResponseAsync`; final branching re-evaluation; write response+answers in one save; stamp completion per tier.
- [ ] **Step 4 — Run, expect PASS.** Render `Views/Survey/ThankYou.cshtml` (`Survey.ThankYou`). **Commit:** `feat(survey): submit response with anonymity tiers`

### Task 4.4: Public-slug answering path

**Files:** `Controllers/SurveyController.cs` (public entry action); reuse `Views/Survey/Intro.cshtml` (slug variant — language picker, no privacy selector).

- [ ] **Step 1 — `GET /Survey/{slug}`** (`[AllowAnonymous]`) → resolve survey by `PublicSlug`; 404 if none; `Closed.cshtml` if not `Open` / past `ClosesAt`. No invitation/token. Responses are forced **Anonymous**, `InputMethod = Slug`. Show intro + **language picker** (default `CultureInfo.CurrentUICulture`); **no** privacy selector (always Anonymous).
- [ ] **Step 2 — Reuse** the Task 4.2 page flow and Task 4.3 submit with `anonymity = Anonymous`, `InputMethod = Slug`, no invitation link. On the first advance, **increment `Survey.PublicStartedCount`** (repo `IncrementPublicStartedAsync(surveyId)`); finishes are submitted `Slug` responses.
- [ ] **Step 3 — Route guard:** `/Survey/Answer` and `/Survey/Admin` must resolve before `{slug}` (literal-segment precedence) and the builder must have rejected those reserved slugs (Task 2.1). Add a controller/route test pinning that `Admin`/`Answer` are not treated as slugs.
- [ ] **Step 4 — Commit:** `feat(survey): public-slug answering path`

---

## Phase 5 — Reminder job

### Task 5.1: `SendSurveyReminderJob` (TDD)

**Files:** `src/Humans.Infrastructure/Jobs/SendSurveyReminderJob.cs`; modify `src/Humans.Web/Extensions/RecurringJobExtensions.cs`; extend `ISurveyService` + repo with the sweep.

- [ ] **Step 1 — Failing test** on `SurveyService.SendDueRemindersAsync(CancellationToken)`: selects invitations where the survey is `Open`, `Completed == false`, `SentAt <= now - 7d`, `ReminderSentAt is null`; enqueues exactly one `SurveyReminder` email per invitee (localised); stamps `ReminderSentAt`. A second run sends none (idempotent).
- [ ] **Step 2 — Repository:** `GetInvitationsDueForReminderAsync(now, threshold)` using the `(SurveyId, Completed, SentAt)` index; `SetReminderSentAsync(id, at)`.
- [ ] **Step 3 — Implement** the service method; then the job class `[DisableConcurrentExecution(300)] public class SendSurveyReminderJob(ISurveyService surveyService, ILogger<SendSurveyReminderJob> logger) : IRecurringJob` whose `ExecuteAsync` calls `surveyService.SendDueRemindersAsync(ct)` (job touches **no** repository).
- [ ] **Step 4 — Register cron** in `RecurringJobExtensions.UseHumansRecurringJobs`:
  `("survey-reminder", () => RecurringJob.AddOrUpdate<SendSurveyReminderJob>("survey-reminder", j => j.ExecuteAsync(CancellationToken.None), "0 9 * * *"))`
- [ ] **Step 5 — Run tests, build.** **Commit:** `feat(survey): 7-day reminder job`

---

## Phase 6 — Results, export, analysis API

### Task 6.1: Results aggregation (TDD)

- [ ] **Step 1 — Failing test** on `SurveyService.GetResultsAsync(surveyId)`: per-question aggregates (choice counts/%, rating distribution, free-text list), response-rate (responses ÷ invited), the **participation funnel** — `started` (link: invitations with `Started=true`; slug: `Survey.PublicStartedCount`) vs `finished` (submitted responses), split by `InputMethod` — and a per-respondent drill-down **limited to `Identified`** responses (others contribute to counts but expose no identity). Free-text returned **as-submitted** (no translation — deferred).
- [ ] **Step 2 — Repository:** `GetResponsesForResultsAsync(surveyId)` (Include Answers only — no cross-section navs exist), `GetInvitedCountAsync`, `GetResponseCountAsync`. Stitch `Identified` respondent names via `IUserServiceRead.GetUserInfosAsync`.
- [ ] **Step 3 — Implement**; **Commit:** `feat(survey): results aggregation`

### Task 6.2: Results view + CSV/JSON export

- [ ] `SurveyAdminController` Results GET → `Views/SurveyAdmin/Results.cshtml` (aggregates + Identified drill-down). `Models/Survey/SurveyResultsBuilder.cs` assembles the VM.
- [ ] CSV export (`Models/Survey/SurveyCsvExportBuilder.cs`): one row per response, one column per question (multi-choice flattened `value|value`), an `anonymity` column, identity columns populated **only** for `Identified`. JSON export: structured response→answers, enums as strings, option `Value`s as join keys. Both as `FileResult` download actions.
- [ ] **Commit:** `feat(survey): results view and CSV/JSON export`

### Task 6.3: Analysis API (key-authed, read-only)

**Files:** modify `Filters/ApiKeyAuthFilter.cs` (add `SurveyApiSettings`+`SurveyApiKeyAuthFilter`); create `Controllers/SurveysApiController.cs`; modify `ConfigurationMetadataExtensions.cs`.

- [ ] **Step 1 — Settings + filter** (mirror Issues):
```csharp
public class SurveyApiSettings { public const string SectionName = "SurveyApi"; public string ApiKey { get; set; } = string.Empty; }
public class SurveyApiKeyAuthFilter(IOptions<SurveyApiSettings> s) : ApiKeyAuthFilterBase(s.Value.ApiKey);
```
  (DI + env var already added in Task 1.5.) Register the indicator: `configRegistry.RegisterEnvironmentVariable("SURVEY_API_KEY", "Survey API", isSensitive: true);`
- [ ] **Step 2 — Controller** `[ApiController][Route("api/surveys")][ServiceFilter(typeof(SurveyApiKeyAuthFilter))]`:
  - `GET /api/surveys` → list (id, title, status, response/invite counts).
  - `GET /api/surveys/{id}` → definition (questions/options/types/branching; option `Value`s as join keys).
  - `GET /api/surveys/{id}/responses` → responses+answers. **`?format=md`** returns a Markdown table (one row per response — the token-lean shape for an agent reading the bulk); default `json`. Also `?anonymity=`, `?since=`, `?limit=&cursor=` paging. **Identity fields only for `Identified`** (enforced server-side regardless of params). _(MD writer: build the column set from the survey's questions; flatten multi-choice to `a|b`; include `anonymity` + `input_method` columns; identity columns only for Identified.)_
  - `GET /api/surveys/{id}/aggregates` → the Task 6.1 aggregates **+ the participation funnel** (started vs finished by `InputMethod`).
  - All read-only; anonymous-object projections inline (Issues pattern); enums serialised as strings.
- [ ] **Step 3 — Test** the 503 (key unset) / 401 (key wrong) behaviour via the filter (small WebApplicationFactory test or filter unit test, matching how Issues/Feedback are tested if at all).
- [ ] **Step 4 — Commit:** `feat(survey): read-only analysis API`

---

## Phase 7 — GDPR, transparency, docs, final green

### Task 7.1: GDPR contributor (TDD)

- [ ] **Step 1 — Add** `public const string SurveyResponses = "SurveyResponses";` to `GdprExportSections`.
- [ ] **Step 2 — Failing test:** `SurveyService.ContributeForUserAsync(userId)` returns a single `UserDataSlice(GdprExportSections.SurveyResponses, …)` containing the user's **`Identified`** responses only (survey title, submitted-at, their answers); `CompletionTracked`/`Anonymous` excluded.
- [ ] **Step 3 — Implement** `IUserDataContributor` on `SurveyService` (interface already added to the class + DI in Task 1.5); repo method `GetIdentifiedResponsesForUserAsync(userId)`.
- [ ] **Step 4 — Run, expect PASS.** **Commit:** `feat(survey): GDPR export contributor`

### Task 7.2: Transparency note + section doc

- [ ] **Step 1 — Wizard intro copy** (resx string, all six cultures): "Your responses may be reviewed and analysed, including by automated tooling, to improve the collective." Render on `Intro.cshtml`.
- [ ] **Step 2 — `docs/sections/survey.md`** per `docs/sections/SECTION-TEMPLATE.md`: concepts, data model, actors/roles (BoardOrAdmin authors; invited members answer), invariants (anonymity encoding table; one reminder; branching server-side; `survey_*` single-repo ownership), negative access rules (no cross-section reach-in; identity only for Identified), triggers (open/close/send/submit/reminder), cross-section deps (`IUserServiceRead`/`ITeamServiceRead`/`ITicketServiceRead`/`IEmailService`/`IEmailMessageFactory`/`IAuditLogService`/`IDataProtectionProvider`/`IUserDataContributor`), architecture status (A — born §15-compliant).
- [ ] **Step 3 — Commit:** `docs(survey): transparency note and section invariant doc`

### Task 7.3: Final verification

- [ ] `dotnet build Humans.slnx -v quiet` → 0 errors/warnings (analyzers HUM0017/HUM0025 clean).
- [ ] `dotnet test Humans.slnx -v quiet` → all green (Survey unit + architecture tests; full suite unaffected).
- [ ] Confirm the single `AddSurveySection` migration is the only schema change and applies cleanly to a fresh DB.
- [ ] **Commit:** `chore(survey): final build/test green` then push.

---

## Self-review against the spec

- **§1–2 goals / decisions:** authoring (0.x,1.x,2.x), translation manual (LocalizedText), audience (3.2), invites (3.4), anonymity (4.3), reminders (5.1), results (6.x) — all covered. Assisted translation & public slug intentionally **out** (§14/§15).
- **§3 data model:** all six tables (Phase 0); `Cultures`/`PublicSlug` dropped per Deviation #6 (noted).
- **§4 anonymity:** encoding table + Task 4.3 tests. **§5 branching:** Tasks 1.2, 4.2, 4.3 (server-side, hidden-required ignored, hidden-answer rejection). **§6 i18n:** LocalizedText + culture switcher; §6.1/§6.2 translation deferred. **§7 distribution + token:** Phase 3 (audience via reads — Deviation #2; token §7.1 via Data Protection — Task 3.1). **§8 wizard:** Phase 4 (resume simplified — Deviation #8, needs confirm). **§9 authz:** `BoardOrAdmin` + `[AllowAnonymous]` + exempt controller (Deviation #1, #7). **§10 cross-section:** all via `I…ServiceRead`/service interfaces. **§11 GDPR:** Task 7.1. **§12 architecture:** no-EF, single-repo, no caching, arch tests (Tasks 1.5, 1.6). **§13 results/export/API:** Phase 6. **§16 acceptance criteria:** mapped across phases; translation-dependent criteria deferred per §15.6.
- **Open confirm for Peter:** Deviation #8 (wizard resume simplification) is the one behavioural cut from the spec — everything else is a mechanism correction or a pre-agreed deferral.
