<!-- freshness:triggers
  src/Sections/Humans.Email/**
  src/Sections/Humans.Email.Contracts/**
-->

# Email — target shape

Regenerated every section-doctor run, before any scan. Diff it against the previous
run's copy: a change means the section moved, or the earlier target was wrong.

## 1. What the section does

Every email the organisation sends to a human leaves through here. A caller elsewhere
hands over finished words — it has already written them, in the reader's language, from
its own copy — and this section decides whether the reader has asked not to receive that
kind of mail, dresses the words in the house wrapper, writes the whole message down in a
durable send log, and hands it to the mail server within the minute. It keeps trying when
the mail server refuses, backs off further each time, gives up after a fixed number of
attempts, and lets an admin watch the queue, stop it, restart a stuck message or throw one
away.

It writes almost nothing itself. The one message it still composes is the relay that
carries one volunteer's words to another, which belongs to nobody else because it names
nothing the organisation does. It also renders — without sending — two kinds of preview: a
finished message, shown to an admin exactly as it would arrive, and whatever a human is
typing right now into any compose box in the app.

Mail a human is sitting waiting for — a login link, a verification code, workspace
credentials — is written down like everything else, but instead of waiting for the next
minute's drain it starts one immediately, and is picked up ahead of the other waiting mail
when that drain runs. It is a shorter wait, not a bypass: a stopped queue still stops it,
and so does an unreachable scheduler, in which case the ordinary tick delivers it within
the minute. The hurry is because everything else drains at one message a second and a
backlog would lock them out.

Mail to a human the organisation has just erased is sent without being written down at all,
because writing it down would recreate the personal data the erasure removed.

The section keeps a second, permanent tally beside the send log: how many messages of each
kind went out or failed on each day. The send log itself is pruned on age, so without the
tally there is no denominator when the mail provider reports a spam-rate spike months later.

Finally, it is where a person sees what the organisation has emailed them, where that
history is handed over on a data-export request, and where it is destroyed on an erasure
request.

## 2. The shapes

Every external entry point, grouped by the question it answers.

| Shape | Surface | Notes |
|---|---|---|
| **Send one message** | `IEmailService.SendAsync` | The single transport path. One method, deliberately. |
| **Describe a message** | `IEmailMessageFactory.FacilitatedMessage` | One method, the person-to-person relay. Every other section builds its own `EmailMessage` in its own `<Section>Emails` (peterdrier/Humans#1651). |
| **Show me a message without sending it** | `IEmailPreviewServiceRead.RenderSystemMessage`; `IEmailPreviewService.RenderMarkdown` (internal) | Two arms of one job: a finished always-send message, and a half-typed Markdown body. The second is internal because only this section's own `EmailPreviewController` serves it. |
| **Put my templates in the gallery** | `IEmailPreviewContributor` | The fan-out that keeps the gallery from naming other sections. Email contributes to it like anyone else. |
| **Which templates cannot wait?** | `TimeSensitiveTemplates` | One list, read by both the immediate drain and the batch order, so they cannot disagree. |
| **What have we sent this person?** | `IEmailOutboxServiceRead` — per-user list, per-user count, dashboard stats | Consumed by Shell's profile and user-admin pages and the admin tile. |
| **Drain the queue / prune the queue** | `IEmailOutboxProcessor`, `IEmailOutboxRetention`, `IImmediateOutboxProcessor` | Job-facing. The first two have no consumer outside the section any more; Shell registers the third. |
| **Admin operates the queue** | `IEmailOutboxService` (internal) behind `/Email/EmailOutbox` and the `/Settings#email` tab — pause, resume, retry, discard, volume, backfill | Section-internal; `EmailController` and the settings tab component are its only callers. |
| **Admin reads the templates** | `/Email/EmailPreview` | Every contributor's samples, rendered in each of the six cultures. |

Inside, the work runs in this order: **send service** (opt-out, headers, wrap, log row) →
**processor** (transport, retry, tally, grant mirror). Three leaves hang off it: the
**body composer** (the branded wrapper and its inline-style pass, shared by send and
preview), the **transport** (SMTP in production, a logging stub otherwise), and the
**factory** for the one template this section still writes.

## 3. Structure

Written fresh from the shapes, not from today's folder listing.

- **`Domain/`** — the outbox row and the daily tally row. Two entities, no behaviour.
- **`Data/`** — the context, the entity configurations, and the one repository that is
  allowed to touch them. Every query the section needs is a named method there.
- **`Services/`** — the send path, the drain, the admin surface, and the leaves above,
  each behind an internal interface.
- **`Contracts/` (leaf project)** — exactly the types other sections name, and the payload
  records those methods take. Nothing else belongs on it.
- **`Jobs/`** — the scheduler shims, with no logic in them.
- **`Controllers/` + `Views/` + `Models/` + `ViewComponents/`** — the two admin pages, the
  settings tab, and the preview endpoint every compose box posts to. No localized copy:
  they are admin-side, except the preview endpoint, which returns no copy of its own.
- **`EmailResource.resx`** and its five language variants — the copy of the one template this section renders, in six
  cultures. Not the pages' copy; the pages have none.
- **`Docs/`** — the invariants, the authorization table, the data-access table, the feature
  spec, and this file.

The shape the code does not yet have: **a name for each of the two things the send log is
asked to be.** It is both the retry queue (short-lived, pruned) and the per-human record of
what was sent (read back on the profile page, exported, erased). Every rule that reads
oddly — why erasure deletes rows the retention sweep would not, why the tally exists at all
— comes from that one noun doing two jobs.

## 4. Invariants

- Every message handed to `IEmailService` writes a send-log row before any transport call,
  except one marked do-not-persist, which goes straight to the transport and is therefore
  never retried (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:79`).
- A recipient who has opted out of a message's category never gets it and no row is written
  (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:53`).
- Every message carries a `Feedback-ID`, and an opt-outable one also carries unsubscribe
  headers and a footer link (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:65`,
  `src/Sections/Humans.Email/Services/OutboxEmailService.cs:69`).
- The pause flag stops the drain and nothing else
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:50`); this section is the
  only place that reads or writes it
  (`src/Sections/Humans.Email/Services/EmailOutboxService.cs:85`).
- Time-sensitive templates are picked up ahead of every other row, and FIFO holds within
  each of the two classes
  (`src/Sections/Humans.Email/Data/EmailOutboxRepository.cs:124`). The same list, not a
  per-message flag, decides which messages also start a drain immediately
  (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:118`).
- A message that fails is retried with exponential backoff
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:123`) until a fixed attempt
  count, then stops being picked up and waits for an admin
  (`src/Sections/Humans.Email/Data/EmailOutboxRepository.cs:118`).
- Delivery is at-least-once, not exactly-once. A transport that hands the message to the
  SMTP server and *then* observes cancellation leaves the row unmarked, and the stale-pickup
  window re-claims it (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:57`) — so
  that message goes out twice. Accepted deliberately: at this scale a rare duplicate is
  cheaper than the `Sending` status or idempotency key that would close it.
- A row's status records what the section did with the message, which is not always a send:
  an address at the local or ticket-stub test domains is marked sent deliberately, without a
  transport call (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:75`).
- Bookkeeping never rewrites delivery state. Neither the campaign-grant mirror
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:159`) nor the daily tally
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:182`) can turn a delivered
  message back into a failed one by throwing.
- The daily tally outlives the send log: the retention sweep only ever deletes from the send
  log (`src/Sections/Humans.Email/Data/EmailOutboxRepository.cs:188`), and the backfill only
  inserts day/template combinations that have no row yet
  (`src/Sections/Humans.Email/Services/EmailOutboxService.cs:165`).
- Only `AdminOnly` reaches the outbox dashboard, the preview gallery and the backfill
  (`src/Sections/Humans.Email/Controllers/EmailController.cs:16`). The Markdown preview
  endpoint is the one exception and is open to any authenticated human, because every
  compose box in the app posts to it
  (`src/Sections/Humans.Email/Controllers/EmailPreviewController.cs:15`).
- No text reaches a body as raw HTML, whoever authored it: both the relay this section
  renders (`src/Sections/Humans.Email/Services/EmailMessageFactory.cs:27`) and the
  compose-box preview (`src/Sections/Humans.Email/Services/EmailPreviewService.cs:30`) go
  through the canonical sanitizing Markdown renderer, and the interpolated values around
  them are HTML-encoded (`src/Sections/Humans.Email/Services/EmailMessageFactory.cs:47`).
- The branded wrapper and its inline-style pass are applied in exactly one place, so a
  preview and a real send cannot render differently
  (`src/Sections/Humans.Email/Services/BrandedEmailTemplate.cs:9`).
- Every string in this section's resource set is present in all six cultures, enforced by
  `tests/Humans.Web.Tests/Resources/SectionResourceParityTests.cs:1`.
- A send-log row is personal data: it is exported under Article 15
  (`src/Sections/Humans.Email/Services/EmailOutboxService.cs:187`) and destroyed under
  Article 17 whatever its status
  (`src/Sections/Humans.Email/Services/EmailOutboxService.cs:219`).
- Only the repository touches the section's `DbContext`, and the entities never leave the
  section (`src/Sections/Humans.Email/Data/EmailOutboxRepository.cs:16`).

## 5. Seams

Specified-but-unbuilt. Not built here, not ranked; recorded because items touching their
future callers are shaped by them.

- **Bounce handling.** A sent row means the SMTP server accepted the message, not that it
  arrived. The section states this explicitly and has no bounce path. Anything that wants
  real delivery outcomes needs a new inbound seam, not a new status value.
- **A moved-inward job contract.** `IEmailOutboxProcessor` and `IEmailOutboxRetention` sit
  on the public leaf but have no consumer outside the section since the jobs came in — Shell
  names neither. They can move inward whenever someone wants the churn.
  `IImmediateOutboxProcessor` cannot follow them: Shell registers its implementation.
- **A failure day in the tally.** Backfilled rows carry no failure count, because the send
  log keeps only a message's final state, not the day of each attempt. Reconstructing
  historical failures needs a per-attempt record that does not exist.

## 6. Deliberately not done

- **No caching decorator.** The outbox is a sequential queue drain, not a hot-path read
  shape.
- **No `Sending` status.** In-flight rows are tracked by a pick-up timestamp, not a status
  transition, so a crashed run recovers by time rather than by state repair.
- **No separate "permanently failed" status.** Failed plus an attempt count answers it.
- **No per-header columns.** New headers are JSON in one column.
- **No FK constraints or navigation properties on the cross-section ids.** A stale id on an
  append-only send log is an accepted orphan.
- **No second `IEmailService` implementation.** There is one send path on purpose.
- **No method per template on the contracts leaf.** A template belongs to the section that
  sends it; the leaf carries the one relay that belongs to nobody.
- **No localized copy on the admin views.** Admin-side views are exempt.

## Load-bearing weirdness

Settled decisions and essential complexity — stop re-litigating these.

- **`EmailSettings` lives in `Humans.Base.Configuration` and is bound in Shell**, not in
  `Section.Register`. Auth, Users, a Consent job and this section's own SMTP health check
  all read it. It is Base configuration the section is merely named after.
- **`EmailOutboxStatus` lives in `Humans.Base.Enums`.** Campaigns and Surveys persist it on
  their own tables, so it is shared vocabulary, not section-internal.
- **`EmailResource.cs` must stay in namespace `Humans.Email`** and sit beside its resx
  files: the SDK derives the manifest name from the adjacent same-named `.cs` file's
  namespace. Moving it to a `Resources` namespace makes every email body fall back to its
  raw key at runtime. It is public because the boot localization diagnostic discovers
  markers through `GetExportedTypes()`.
- **`IEmailOutboxService` survives the internalise pass** even though its only consumers are
  inside the section: the concrete service is sealed and the test doubles need an interface
  to substitute.
- **The repository is a Singleton over `IDbContextFactory`** so the same instance serves
  Scoped services and the recurring drain alike.
- **Load-mutate-save instead of `ExecuteUpdate`/`ExecuteDelete`** in the repository's
  bulk-write methods, so the EF InMemory provider the unit tests use still exercises the
  path. Each carries a comment saying so.
- **`HangfireImmediateOutboxProcessor` is public under `Contracts/`** because Shell
  registers it, and it is not an `IRecurringJob`, so the `Jobs/` carve-out does not claim it.
- **The 1-second throttle between sends** is a mail-server rate limit, not an arbitrary
  sleep; it is what makes queue priority matter at all.
- **The tally is written outside the transaction that marks the message**, in its own
  context, and a failure to write it is logged and swallowed. A crash between the two drops
  one message from that day's count. Accepted: an undercounted day is cheaper than resending
  mail because a counter row would not save.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-04 | First doctoring: unsanitized markdown in the feedback-response and issue-comment bodies, dead resource keys retired, doc set corrected against deleted projects | peterdrier/Humans#1587 |
| section-doctor | 2026-09-22 | Failure-path grant mirror guarded, dead backfill query arm cut, stale comments and the four section docs realigned after the template move | peterdrier/Humans#1790 |
