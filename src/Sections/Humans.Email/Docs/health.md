<!-- freshness:triggers
  src/Sections/Humans.Email/**
  src/Sections/Humans.Email.Contracts/**
-->

# Email — target shape

Regenerated every section-doctor run, before any scan. Diff it against the previous
run's copy: a change means the section moved, or the earlier target was wrong.

## 1. What the section does

Every email the organisation sends to a human leaves through here. A caller elsewhere
hands over finished words, already written in the reader's language from its own copy,
and this section checks whether the reader has asked not to receive that kind of mail,
dresses the words in the house wrapper, writes the whole message into a durable send log,
and hands it to the mail server within the minute. It keeps trying when the mail server
refuses, waits longer each time, gives up after a fixed number of attempts, and lets an
admin watch the queue, stop it, restart a stuck message or throw one away.

It writes almost nothing itself. The one message it still composes is the relay that
carries one volunteer's words to another, and it owns the page where a volunteer types
that message. It also renders, without sending, a finished message exactly as it would
arrive, and whatever a human is typing into any compose box in the app; that compose box
can also send the draft to its author's own address.

Mail a human is waiting for (a login link, a verification code, workspace credentials)
is written down like everything else but starts a drain at once and is picked up ahead of
other waiting mail. It is a shorter wait, not a bypass: a stopped queue still stops it.

Mail to a human the organisation has just erased is sent without being written down,
because writing it down would recreate the personal data the erasure removed.

Beside the send log it keeps a permanent tally of how many messages of each kind went out
or failed on each day, because the send log is pruned on age and a spam-rate report months
later needs a denominator.

It holds each person's answer to "which kinds of mail do you want?": one email switch and
one in-app inbox switch per kind. Members change the email switch on their own page;
a reader arriving from an unsubscribe link, or a signed-in account with no profile,
changes both switches on a page that needs no login. The links in every message that let
a reader say no must keep working for as long as they live, so their format never changes.
Until a one-time copy has run, a person with no answer recorded here reads the answer
Users still holds from before the move.

Finally, it is where a person sees what the organisation has emailed them, where an admin
sees the same for anyone, where that history is handed over on a data-export request, and
where it and the preferences are destroyed on an erasure request or folded into the
surviving account on a merge.

## 2. The shapes

| Shape | Surface | Notes |
|---|---|---|
| **Send one message** | `IEmailService.SendAsync` | The single transport path. |
| **Describe the relay** | `IEmailMessageFactory.FacilitatedMessage` | The one template Email writes; every other section builds its own `EmailMessage`. |
| **Relay one volunteer's words** | `/Email/SendMessage/{id}` | The member-facing composer for the relay. Sending as a team is Teams'. |
| **Show me a message without sending it** | `IEmailPreviewService.RenderSystemMessage`; `/Email/PreviewMarkdown`; `/Email/SendMarkdownToSelf` | A finished always-send message; a half-typed Markdown body; that body sent to its author only. |
| **Put my templates in the gallery** | `IEmailPreviewContributor`, `/Email/EmailPreview` | The fan-out keeps the gallery from naming other sections. |
| **Which templates cannot wait?** | `TimeSensitiveTemplates` | One list, read by the immediate drain and the batch order. |
| **What have we sent this person?** | `/Email/Outbox`, `/Email/Admin/{id}/Outbox`, the admin-detail outbox card | All inside Email. |
| **Drain and prune the queue** | `ProcessEmailOutboxJob`, `CleanupEmailOutboxJob`, `IImmediateOutboxProcessor` | Shell registers the immediate trigger. |
| **What kinds of mail does this person accept?** | `ICommunicationPreferenceService`; `ICommunicationPreferenceWrite` (MailerLite's import) | Read by Notifications, MailerLite, Debug, Users and Teams. |
| **Change my mind** | `/Email/CommunicationPreferences`, `/Email/Guest/CommunicationPreferences`, the admin-detail preferences card | Own page, no-login page, read-only admin card. |
| **Say no without logging in** | `/Email/Unsubscribe/{token}`, `/Email/Unsubscribe/OneClick` | Token purposes and payload are frozen. |
| **Admin operates the queue** | `/Email/EmailOutbox` and its backfill, the `/Settings#email` tab | Pause, resume, retry, discard, volume, backfill. |
| **Account lifecycle** | `IUserDataContributor`, `IUserMerge` | Export, erasure and merge for both the send log and the preferences. |

Inside, a send runs **send service** (opt-out, headers, wrap, log row) → **processor**
(transport, retry, tally, grant mirror). The **body composer** (wrapper plus inline-style
pass), the **transport** (SMTP or a logging stub) and the **relay factory** hang off it.
The preference lane is **preference service** (rows here, legacy fallback, ticket lock)
→ **token provider**, with the **unsubscribe service** reading tokens on its behalf.

## 3. Structure

- **`Domain/`**: the send-log row, the daily tally row, the preference row. No behaviour.
- **`Data/`**: one context, its configurations, and two repositories (send log plus tally;
  preferences). Every query is a named method there.
- **`Services/`**: the send path, the drain, the admin surface, the preference and
  unsubscribe services, the self-send, and the leaves, each behind an internal interface
  except the static helpers.
- **Contracts leaf**: only the types another section names, and the records they take.
  Interfaces nothing outside Email names belong inside the section.
- **`Jobs/`**: scheduler shims, no logic.
- **`Controllers/` + `Views/` + `Models/` + `ViewComponents/`**: the admin pages, the member
  and guest preference pages, the unsubscribe pages, the member outbox, the relay composer,
  the preview endpoints, the settings tab and the admin-detail cards.
- **`EmailResource.resx`** and its five language variants: the copy of the relay, the footer,
  the self-test subject, and every member-facing page. The admin views carry none.
- **`Docs/`**: invariants, authorization, data access, two feature specs, this file.

The shape the code does not yet have: **one builder for the preference table.** The
member panel and the no-login page each turn the same rows into the same per-category
items; the no-login page adds the inbox column and the highlighted row. Today that loop is
written twice.

## 4. Invariants

- Every message handed to `IEmailService` writes a send-log row before any transport call,
  except one marked do-not-persist, which goes straight to the transport and is never
  retried (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:76`).
- A recipient opted out of a message's category never gets it and no row is written
  (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:38`).
- Every message carries a `Feedback-ID`, and an opt-outable one also carries unsubscribe
  headers and a footer link (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:49`,
  `src/Sections/Humans.Email/Services/OutboxEmailService.cs:53`).
- The pause flag stops the drain and nothing else
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:38`).
- Time-sensitive templates are picked up first, FIFO within each class
  (`src/Sections/Humans.Email/Data/EmailOutboxRepository.cs:129`), and the same list starts
  an immediate drain (`src/Sections/Humans.Email/Services/OutboxEmailService.cs:116`).
- A failed message is retried with exponential backoff
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:91`) until a fixed attempt
  count, then waits for an admin (`src/Sections/Humans.Email/Data/EmailOutboxRepository.cs:124`).
- Delivery is at-least-once: a row picked up and not marked is re-claimed after the stale
  window (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:45`).
- A test-domain address is marked sent without a transport call
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:61`).
- Bookkeeping never rewrites delivery state: neither the grant mirror
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:138`) nor the tally
  (`src/Sections/Humans.Email/Services/EmailOutboxProcessor.cs:155`) can throw out of the batch.
- The tally outlives the send log: retention deletes only old sent rows
  (`src/Sections/Humans.Email/Data/EmailOutboxRepository.cs:197`), and the backfill inserts
  only day/template keys with no row yet
  (`src/Sections/Humans.Email/Services/EmailOutboxService.cs:163`).
- Only `AdminOnly` reaches the outbox dashboard, the gallery and the backfill
  (`src/Sections/Humans.Email/Controllers/EmailController.cs:17`); the preview and
  self-send endpoints are open to any signed-in human
  (`src/Sections/Humans.Email/Controllers/EmailPreviewController.cs:15`), and the self-send
  is rate-limited (`src/Sections/Humans.Email/Controllers/EmailPreviewController.cs:41`).
- The self-send reaches only the caller's own notification address
  (`src/Sections/Humans.Email/Services/ComposerSelfSendService.cs:25`).
- The relay refuses a self-send and an opted-out recipient, on GET and POST alike
  (`src/Sections/Humans.Email/Controllers/FacilitatedMessageController.cs:47`,
  `src/Sections/Humans.Email/Controllers/FacilitatedMessageController.cs:81`), and audits
  each send (`src/Sections/Humans.Email/Controllers/FacilitatedMessageController.cs:116`).
- No human-typed text reaches a body as raw HTML: the relay, the preview and the self-send
  render through the sanitizing Markdown renderer
  (`src/Sections/Humans.Email/Services/EmailMessageFactory.cs:17`,
  `src/Sections/Humans.Email/Services/EmailPreviewService.cs:26`,
  `src/Sections/Humans.Email/Services/ComposerSelfSendService.cs:35`), and the values around
  the relay are HTML-encoded (`src/Sections/Humans.Email/Services/EmailMessageFactory.cs:37`).
- The wrapper and inline-style pass are applied in one place, so a preview and a send
  cannot differ (`src/Sections/Humans.Email/Services/BrandedEmailTemplate.cs:14`).
- Always-on categories cannot be changed by any write
  (`src/Sections/Humans.Email/Services/CommunicationPreferenceService.cs:137`), and the
  member and guest toggles refuse to change Ticketing for a matched ticket holder
  (`src/Sections/Humans.Email/Services/CommunicationPreferenceService.cs:117`).
- An unchanged preference write writes nothing and audits nothing
  (`src/Sections/Humans.Email/Services/CommunicationPreferenceService.cs:163`); every
  change is audited (`src/Sections/Humans.Email/Services/CommunicationPreferenceService.cs:183`).
- A cleared row shadows the legacy row and reads as nothing recorded
  (`src/Sections/Humans.Email/Services/CommunicationPreferenceService.cs:99`).
- Unsubscribe tokens keep their purpose, payload and lifetime
  (`src/Sections/Humans.Email/Services/UnsubscribeTokenProvider.cs:15`,
  `src/Sections/Humans.Email/Services/UnsubscribeTokenProvider.cs:23`), and a token for an
  erased account is refused even inside its lifetime
  (`src/Sections/Humans.Email/Services/UnsubscribeService.cs:21`,
  `src/Sections/Humans.Email/Controllers/GuestAccountController.cs:105`).
- A send-log row is personal data: exported
  (`src/Sections/Humans.Email/Services/EmailOutboxService.cs:178`) and destroyed whatever
  its status (`src/Sections/Humans.Email/Services/EmailOutboxService.cs:229`); erasure
  deletes every preference row too
  (`src/Sections/Humans.Email/Services/CommunicationPreferenceService.cs:416`).
- A merge leaves the source with only cleared rows where a legacy row backs it
  (`src/Sections/Humans.Email/Services/CommunicationPreferenceService.cs:371`).
- One row per person and category
  (`src/Sections/Humans.Email/Data/CommunicationPreferenceConfiguration.cs:30`).
- Every string in this section's resource set is present in all six cultures, enforced by
  `tests/Humans.Web.Tests/Resources/SectionResourceParityTests.cs:1`.

## 5. Seams

- **Bounce handling.** A sent row means the mail server accepted the message. Real
  delivery outcomes need a new inbound seam, not a new status value.
- **A failure day in the tally.** Backfilled rows carry no failure count; reconstructing
  them needs a per-attempt record that does not exist.
- **Retiring the legacy fallback.** Once the copy has run in production, the fallback, the
  cleared rows and Users' frozen table go in one PR.

## 6. Deliberately not done

- **No caching decorator.** The outbox is a sequential drain; preference reads are small.
- **No `Sending` status.** In-flight rows are tracked by a pick-up timestamp.
- **No separate "permanently failed" status.** Failed plus an attempt count answers it.
- **No per-header columns.** New headers are JSON in one column.
- **No FK constraints or navigation properties on cross-section ids.**
- **No second `IEmailService` implementation.**
- **No method per template on the contracts leaf.**
- **No localized copy on the admin views.**
- **No inbox switch on the member's own preference page.** The member page changes email
  only and carries the stored inbox value through unchanged.

## Load-bearing weirdness

- **`EmailSettings` lives in `Humans.Base.Configuration` and is bound in Shell.** Auth,
  a Consent job and this section all read it.
- **`EmailOutboxStatus` lives in `Humans.Base.Enums`.** Campaigns and Surveys persist it.
- **`EmailResource.cs` must stay in namespace `Humans.Email`** beside its resx files: the SDK
  derives the manifest name from it. It is public because the boot localization diagnostic
  discovers markers through `GetExportedTypes()`.
- **`CommunicationPreferencesPanelViewComponent` is public** because pages invoke it through
  the `<vc:>` tag helper, which only finds public components.
- **`IEmailOutboxManagement` and `ICommunicationPreferenceManagement` exist** although every
  consumer is inside the section: the concrete services are sealed and the test doubles need
  an interface.
- **Both repositories are Singletons over `IDbContextFactory`**, so Scoped services and the
  recurring drain share them.
- **Load-mutate-save instead of `ExecuteUpdate`/`ExecuteDelete`**, so the EF InMemory
  provider the unit tests use still exercises the path.
- **`HangfireImmediateOutboxProcessor` is public under `Contracts/`** because Shell
  registers it, and it is not an `IRecurringJob`.
- **The 1-second throttle between sends** is a mail-server rate limit.
- **The tally is written outside the transaction that marks the message.** A crash between
  the two drops one message from that day's count; accepted.
- **Unsubscribe links do not pass the Ticketing lock.** A reader who asks to stop is
  stopped; only the toggle pages enforce the lock.
- **The legacy campaign token** (`CampaignUnsubscribe` purpose) still validates and acts on
  Marketing, because links minted before category tokens are still in inboxes.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-04 | First doctoring: unsanitized markdown in the feedback-response and issue-comment bodies, dead resource keys retired, doc set corrected against deleted projects | peterdrier/Humans#1587 |
| section-doctor | 2026-09-22 | Failure-path grant mirror guarded, dead backfill query arm cut, stale comments and the four section docs realigned after the template move | peterdrier/Humans#1790 |
