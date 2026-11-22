# Pleo — Health

## 1. What the section does

Keeps a local copy of the association's Pleo card wallet so the finance people can check it
before any of it is booked. Every night, or when a finance admin presses Sync now, the app reads
all of Pleo's card spending, the chart of accounts, the spending categories, the cardholders and
the wallet balance, and replaces its copy in one go; a read that fails leaves yesterday's copy in
place and says why. The pages show whether the copy adds up to Pleo's own balance, which spend is
still missing a receipt or a ledger account, and every entry with its receipt fetched live from
Pleo. Finance admins decide which ledger account each spending category books to; the nightly
read fills in what nobody linked by hand, and never changes a link a person made. The finance section reads
the settled entries and the balance from here to book them. Nothing is ever written to Pleo.

## 2. The shapes

| Shape | The question it answers | Where it is answered |
|---|---|---|
| S1 — refresh the copy | "Bring the mirror up to date with Pleo." | `pleo-sync` job, `POST /Pleo/SyncNow`: one `Service.SyncAsync` |
| S2 — does it add up | "Does the mirror match Pleo's balance, and what blocks booking?" | `GET /Pleo` |
| S3 — look at entries | "Which entries are these, and what does this one's receipt show?" | `GET /Pleo/Entries`, `GET /Pleo/Entries/{id}`, `GET /Pleo/Entries/{id}/Receipts/{receiptId}` |
| S4 — map categories | "Which ledger account does this spending category book to?" | `GET /Pleo/Categories`, `POST /Pleo/Categories/Link`, `Unlink`, `Add` |
| S5 — read for booking | "What settled in this window, what is the wallet balance, and give me this receipt" | `IPleoService` (Finance) |

S2, S3, S5 and the read half of S4 are one load of the whole mirror (in `Service`) projected
per page; nothing is queried selectively. S1 is the only writer of the cache tables, S4's POSTs the
only hand writer of `pleo_categories`.

## 3. Structure

```
Humans.Pleo/
  Section.cs              DI, config keys, admin nav, the nightly job
  Contracts/              IPleoService + its three records (S5)
  Controllers/            S1 button, S2, S3, S4; ordering of every list
  Services/
    Service               IPleoService + IPleoAdminService: category writes, S1 sync, every read
    PleoClient            the read-only HTTP connector, strict pager
  Domain/                 entities and the sign/kind rules
  Data/                   repository, context, configs, migrations
  Jobs/                   the nightly wrapper: 4xx ends without retry
  Models/ Views/          the four pages
```

The derivation matches the layout: one writer, one reader, one connector, and a facade the DI
and the controller see.

## 4. Invariants

1. Nothing is written to Pleo: the connector issues only GETs and `:search` POSTs
   (`src/Sections/Humans.Pleo/Services/PleoClient.cs:43`, `:52`, `:60`, `:68`, `:75`, `:100`).
2. A sweep reads everything before it writes anything, and writes the four cache tables plus the
   changed category rows in one save (`src/Sections/Humans.Pleo/Services/Service.cs`,
   `:47`; `src/Sections/Humans.Pleo/Data/Repository.cs:30`).
3. A short read never shrinks the mirror: a page without a `data` array, without a boolean
   `hasNextPage`, without a cursor when more is promised, with a repeated cursor, or past the
   page cap throws (`src/Sections/Humans.Pleo/Services/PleoClient.cs:155`, `:163`, `:170`, `:172`,
   `:174`; `PleoClientTests.Pager_rejects_unknown_completion_instead_of_returning_a_partial_dataset`).
4. A category row a person linked is never changed by a sweep, and a sweep never deletes or
   unlinks a category row (`src/Sections/Humans.Pleo/Services/Service.cs`;
   `src/Sections/Humans.Pleo/Data/Repository.cs:22`;
   `PleoSyncTests.Sweep_links_and_adds_categories_from_Pleos_mapping_but_leaves_a_hand_link`).
5. A failed category read does not stop the sweep: categories stay as they are and the error is
   kept on the sync row (`src/Sections/Humans.Pleo/Services/Service.cs`;
   `PleoSyncTests.A_failed_category_fetch_still_mirrors_entries_and_reports_the_error`).
6. One sweep at a time; a second caller is told it was skipped
   (`src/Sections/Humans.Pleo/Services/Service.cs`).
7. A 4xx ends the nightly job without retry; anything else keeps Hangfire's retries
   (`src/Sections/Humans.Pleo/Jobs/PleoSyncJob.cs:20`;
   `PleoSyncJobTests.Permanent_failure_completes_the_job_so_Hangfire_does_not_retry`).
8. An entry's EUR amount is Pleo's EUR bill value when there is one; otherwise an EUR transaction
   value signed by its family; an unclassified family or another currency has none
   (`src/Sections/Humans.Pleo/Domain/PleoEntryRules.cs:29`).
9. Pending entries are listed and never counted
   (`src/Sections/Humans.Pleo/Services/Service.cs`, `:116`).
10. An entry's ledger account comes only from a linked category; anything else is unmapped
    (`src/Sections/Humans.Pleo/Services/Service.cs`).
11. A receipt is served only for an entry in the mirror, only from an `https` `*.pleo.io` URL,
    without the API key, as an image, a PDF or an opaque download
    (`src/Sections/Humans.Pleo/Services/Service.cs`;
    `src/Sections/Humans.Pleo/Services/PleoClient.cs:110`, `:120`;
    `PleoClientTests.Receipt_file_is_downloaded_from_the_signed_url_without_the_key`).
12. Board reads every page; only finance admins and admins sync or change a category
    (`src/Sections/Humans.Pleo/Controllers/PleoController.cs:11`, `:30`, `:108`, `:115`, `:121`).
13. Every category link, unlink and add, by hand or by a sweep, writes an audit entry
    (`src/Sections/Humans.Pleo/Services/Service.cs:65`, `:79`, `:98`;
    `src/Sections/Humans.Pleo/Services/Service.cs`).

## 5. Seams

- **Cardholders bound to members.** Cardholders are mirrored by Pleo id and name only; binding them
  to members is a later step that brings the export and deletion paths with it.

## 6. Deliberately not done

- **No webhooks.** Pleo's webhooks cover only export jobs and vendors; the mirror polls.
- **No link into Pleo's app.** No stable URL pattern is verified.
- **No localization.** `/Pleo` is an English-only finance-admin screen, like `/Holded`.
- **No unique index on a category name.** The service refuses a duplicate
  (`unique-constraints-ids-only`).
- **No GDPR contributor.** Cardholders are bound to no member (see §5).

## Load-bearing weirdness

- **Pleo's `accountId` on an entry is a category id**, not a ledger account. The mirror stores it
  as `CategoryId`, and `pleo_categories` maps it to a Holded code.
- **`pleo_categories` is not a cache** while the other five tables are: it carries hand decisions,
  so the sweep only adds to it.
- **`Service` forwards most calls.** The reader and the sweep were split out of it to keep each
  under one concern; the facade keeps DI and the controller on two interfaces.
- **Receipts are fetched twice per view**: once to list them on the entry page, once per file,
  because Pleo's signed URLs expire.

## History

| Date | Outcome | PR |
|---|---|---|
| 2026-11-19 | First run. Docs trued to the sweep's category writes, the real dependencies and the code's structure; stale comments and the sync card's call count corrected; the sweep gate and the hand unlink/add audit pinned; two behaviours queued for Peter (a hand unlink is undone by the next sweep; blocking counts and their lists disagree) | peterdrier/Humans#2442 |
