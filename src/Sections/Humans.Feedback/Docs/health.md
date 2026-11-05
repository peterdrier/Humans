<!-- freshness:triggers
  src/Sections/Humans.Feedback/**
-->

# Feedback — Target Shape

The shape this section is converging on, regenerated every section-doctor run and diffed
against the previous run's copy. Not a history: what the section *should* be today.

## 1. What the section does

Nothing for users. Feedback was replaced by Issues and removed. What is left is a stub that
drops the old tables on any database that still has them and keeps two kinds of old records
readable: past audit entries and leftover inbox notifications. The target after production
has applied the drop is no section at all.

## 2. The shapes

| Shape | Members | What the caller is asking |
|---|---|---|
| **Drop the tables** | `FeedbackDbContext`, `FeedbackDbContextFactory`, `DropFeedbackTables` | "Bring this database to the state where Feedback's tables are gone." |
| **Keep old records resolving** | `FeedbackAuditActions`, `FeedbackNotificationSources` | "What is this stored audit action or notification source called?" |
| **Be a section** | `Section` | "What does this assembly register at startup?" |

## 3. Structure

```
Section.cs                         registers the two retired declarations and the empty context
FeedbackAuditActions.cs            four retired audit action names
FeedbackNotificationSources.cs     one retired notification source
Data/FeedbackDbContext.cs          maps nothing; carries the migrations
Data/FeedbackDbContextFactory.cs   design-time factory, same history table as runtime
Data/Migrations/                   baseline, reply-email handoff, the drop (all shipped, immutable)
Docs/                              Feedback.md (invariants), health.md (this)
```

A plain class library: no views, no controllers, no Razor.

## 4. Invariants

- The context maps no entities: `src/Sections/Humans.Feedback/Data/FeedbackDbContext.cs:9`.
- The stored notification source `FeedbackResponse` stays declared and retired, so the nightly
  purge deletes its unresolved rows:
  `src/Sections/Humans.Feedback/FeedbackNotificationSources.cs:9`, registered at
  `src/Sections/Humans.Feedback/Section.cs:20`, purged at
  `src/Sections/Humans.Notifications/Services/NotificationInboxService.cs:160`.
- The stored audit action names stay declared:
  `src/Sections/Humans.Feedback/FeedbackAuditActions.cs:8`, registered at
  `src/Sections/Humans.Feedback/Section.cs:19`; pinned by `AuditActionNameStabilityTests`.

## 5. Seams

none

## 6. Deliberately not done

- **No test project.** There is no behaviour left to test; the stability test in
  `tests/Humans.Web.Tests` pins the one thing that matters.
- **No `Docs/debt.yml`.** The only open item is deleting the stub, which the section doc
  already names and which waits on production.

## Load-bearing weirdness

- **An empty `DbContext` registered with sentinel table `feedback_reports`.** It exists only so
  the section migration runner applies `DropFeedbackTables`; the sentinel matters only on a
  database whose history table is missing.
- **Retired declarations outlive the feature.** Deleting them before their stored rows are gone
  (notifications) or rehomed (audit names) leaves old inbox rows unpurged and fails
  `AuditActionNameStabilityTests`.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-11-05 | First doctor pass on the removal stub: the leftover Razor project setup cut, and stale Feedback mentions cleared from docs and Base comments | peterdrier/Humans#2304 |
