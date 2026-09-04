<!-- freshness:triggers
  src/Sections/Humans.Email/**
  src/Sections/Humans.Email.Contracts/**
-->

# Email — target shape

Regenerated every section-doctor run, before any scan. Diff it against the previous
run's copy: a change means the section moved, or the earlier target was wrong.

## 1. What the section does

Every email the organisation sends to a human goes through here. A caller elsewhere
says *what* it wants sent — "this person's application was approved", "here is your
magic link" — and this section turns that into finished words in the reader's own
language, decides whether the reader has asked not to receive that kind of mail,
records the message in a durable send log, and hands it to the mail server a minute
later. It keeps trying when the mail server refuses, gives up after a fixed number of
attempts, and lets an admin watch the whole queue, pause it, retry a stuck message or
throw one away.

Two kinds of mail get special treatment. Mail a human is *sitting waiting for* — a
login link, a verification code, workspace credentials — is written down like
everything else, but instead of waiting for the next minute's drain it starts one
straight away, and it is picked up ahead of the other waiting mail when that drain
runs. It is a shorter wait, not a bypass: a paused queue still stops it, and so does
an unreachable scheduler, in which case the ordinary tick delivers it within the
minute. Event-lifecycle mail gets half of that treatment — it starts a drain
straight away but is not ordered ahead of anything once the drain runs — because
the two switches are set independently. That split is unintended; which half
should change is a decision for Peter. The hurry is because everything else drains at one message a second and a
backlog would lock them out. Mail to a human the organisation has just erased is sent
without being written down at all, because writing it down would recreate the personal
data the erasure just removed.

The section is also the place a person can see what the organisation has emailed
them, and the place that hands their mail history over on a data-export request and
destroys it on an erasure request.

## 2. The shapes

Every external entry point, grouped by the question it answers.

| Shape | Surface | Notes |
|---|---|---|
| **Send one message** | `IEmailService.SendAsync(EmailMessage)` | The single transport path. One method, deliberately. |
| **Describe a message** | `IEmailMessageFactory` — one typed builder per message type | The whole cross-section width of the section: every other section names a builder here rather than composing an `EmailMessage` itself. |
| **Show me a message without sending it** | `IEmailPreviewServiceRead.RenderSystemMessage` | Always-send system messages only; opt-outable ones are refused because their footer is recipient-specific. |
| **What have we sent this person?** | `IEmailOutboxServiceRead` — per-user list, per-user count, dashboard stats | Consumed by Shell's profile and user-admin pages and the admin tile. |
| **Drain the queue / prune the queue** | `IEmailOutboxProcessor`, `IEmailOutboxRetention`, `IImmediateOutboxProcessor` | Job-facing. The first two have no consumer outside the section any more; Shell registers the third. |
| **Admin operates the queue** | `IEmailOutboxService` (internal) behind `/Email/EmailOutbox` — pause, resume, retry, discard | Section-internal; `EmailController` is its only caller. |
| **Admin reads the templates** | `/Email/EmailPreview` | A gallery of rendered templates across the six cultures. |

Inside, the work runs in this order: **renderer** (words) →
**factory** (words + routing policy) → **send service** (opt-out, wrap, log row) →
**processor** (transport, retry, mirror). Two leaves hang off it: the **body
composer** (the branded wrapper, shared by send and preview) and the **transport**
(SMTP in production, a logging stub otherwise).

## 3. Structure

Written fresh from the shapes, not from today's folder listing.

- **`Domain/`** — the outbox row, and the list of template names whose delivery is
  time-sensitive. Nothing else; there is one entity.
- **`Data/`** — the context, the entity configuration, and the one repository that is
  allowed to touch it. Every query the section needs is a named method here.
- **`Services/`** — the collaborators and leaves above, each behind an internal
  interface, plus the internal admin surface.
- **`Contracts/` (leaf project)** — exactly the types other sections name, and the
  payload records those methods take. Nothing else belongs on it.
- **`Jobs/`** — the scheduler shims, with no logic in them.
- **`Controllers/` + `Views/` + `Models/`** — the two admin pages. No localized copy:
  they are admin-side.
- **`EmailResource.*.resx`** — the transactional email copy in six cultures. This is
  *every section's* email text, kept here because this section owns the only renderer
  of it.
- **`Docs/`** — the invariants, the authorization table, the data-access table, the
  feature specs, and this file.

The implied shape the code does not yet have: **every template's words live in the
resx set.** Templates rendered from string literals in the renderer are outside the
structure, not an exception within it.

## 4. Invariants

- Every message sent through `IEmailService` writes an outbox row before any transport
  attempt — except `DoNotPersist`, which writes none and is never retried.
- The pause flag stops the drain and nothing else; it is never read or written from
  outside the section.
- Time-sensitive templates are picked up ahead of every other row, and FIFO holds
  within each of the two classes.
- A message that fails is retried with exponential backoff until a fixed attempt
  count, then stops being picked up and waits for an admin.
- A message that succeeds is never sent twice.
- A row's `Status` and its campaign-grant mirror agree with each other. They record what
  the section did with the message, which is not always a send: an address at `@localhost`
  or `@ticketstub.local` is marked `Sent` deliberately, without a transport call.
- Only `AdminOnly` reaches the outbox dashboard and the preview gallery. A human sees
  their own outbox and nobody else's.
- Every value interpolated into an email body is either HTML-encoded or passed through
  the canonical sanitizing markdown renderer. No text authored by a member reaches a body
  as raw HTML. The one body that is not a template with values interpolated into it — a
  campaign's `EmailBodyTemplate`, which *is* the copy — is `AdminOnly`-authored and
  rendered as markdown with raw HTML intact, on purpose; the codes and names substituted
  into it are encoded.
- Every user-facing string in an email body resolves through `EmailResource`, in all
  six cultures.
- An outbox row is personal data: it is exported under Article 15 and destroyed under
  Article 17, whatever its status.
- Only the repository touches `EmailDbContext`; the entity never leaves the section.

## 5. Seams

Specified-but-unbuilt. Not built here, not ranked; recorded because items touching
their future callers are shaped by them.

- **Bounce handling.** `Sent` means the SMTP server accepted the message, not that it
  arrived. The section states this explicitly and has no bounce path. Anything that
  wants real delivery outcomes needs a new inbound seam, not a new status value.
- **Per-signup notification dedup.** The schema carries a `ShiftSignupId` column and a
  filtered index for "one email of each template per signup". Nothing writes the column
  and nothing queries the index; the dedup was specified and never built.
- **A moved-inward job contract.** `IEmailOutboxProcessor` and `IEmailOutboxRetention`
  sit on the public leaf but have no consumer outside the section since the jobs came in —
  Shell names neither. They can move inward whenever someone wants the churn.
  `IImmediateOutboxProcessor` cannot follow them: Shell registers its implementation.

## 6. Deliberately not done

- **No caching decorator.** The outbox is a sequential queue drain, not a hot-path read
  shape.
- **No `Sending` status.** In-flight rows are tracked by `PickedUpAt`, not a status
  transition, so a crashed run recovers by time rather than by state repair.
- **No separate "permanently failed" status.** `Failed` plus `RetryCount` answers it.
- **No per-header columns.** New headers are JSON in `ExtraHeaders`.
- **No FK constraints or navigation properties on the cross-section ids.** A stale id
  on an append-only send log is an accepted orphan.
- **No second `IEmailService` implementation.** There is one send path on purpose.
- **No localized copy on the two admin views.** Admin-side views are exempt.

## Load-bearing weirdness

Settled decisions and essential complexity — stop re-litigating these.

- **`EmailSettings` lives in `Humans.Base.Configuration` and is bound in Shell**, not in
  `Section.Register`. Auth, Users, a Consent job and this section's own SMTP health
  check all read it. It is Base configuration the section is merely named after.
- **`EmailOutboxStatus` lives in `Humans.Base.Enums`.** Campaigns and Surveys persist it
  on their own tables, so it is shared vocabulary, not section-internal.
- **`EmailResource.cs` must stay in namespace `Humans.Email`** and sit beside its resx
  files: the SDK derives the manifest name from the adjacent same-named `.cs` file's
  namespace. Moving it to a `Resources` namespace makes every email body fall back to
  its raw key at runtime. It is public because the boot localization diagnostic
  discovers markers through `GetExportedTypes()`.
- **`IEmailOutboxService` survives the internalise pass** even though its only consumer
  is the section's own controller: the concrete service is sealed and the test doubles
  need an interface to substitute.
- **The repository is a Singleton over `IDbContextFactory`** so the same instance serves
  Scoped services and the recurring drain alike.
- **Load-mutate-save instead of `ExecuteUpdate`/`ExecuteDelete`** in the repository's
  bulk-write methods, so the EF InMemory provider the unit tests use still exercises the
  path. Each carries a comment saying so.
- **`HangfireImmediateOutboxProcessor` is public under `Contracts/`** because Shell
  registers it, and it is not an `IRecurringJob`, so the `Jobs/` carve-out does not
  claim it.
- **The 1-second throttle between sends** is a mail-server rate limit, not an
  arbitrary sleep; it is what makes queue priority matter at all.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-04 | First doctoring: unsanitized markdown in the feedback-response and issue-comment bodies, dead resource keys retired, doc set corrected against deleted projects | peterdrier/Humans#1587 |
