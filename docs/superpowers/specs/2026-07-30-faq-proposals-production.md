# Agent FAQ proposals — production, 2026-07-30

Source: `/triage agent` against production. All 142 agent conversations (2026-05-03 → 2026-07-28)
were fetched and read, not just the ones the API filters flag.

## How this run differed from the last one

The `refusalsOnly=true` filter returned **zero** results. That filter is not a knowledge-gap
signal — `RefusalReason` is only written for rate-limiting and abuse flags
(`AgentService.cs:75,82`), never for "I don't know". `handoffsOnly=true` returned 7 legacy rows;
`HandedOffToFeedbackId` is hardcoded `null` on every new message (`AgentService.cs:236`), so it
only ever surfaces pre-Issues-migration data. Neither filter is usable for KB triage as things
stand. Reading the transcripts is the only reliable method today.

The `SectionHelpContent.Faq` block added by the 2026-05-23 audit did its job: ticket transfer,
early entry, shift bail and profile completion were all failing before it and answer cleanly
after. The proposals below cover what is still failing in June–July.

## Outcome of this run

Most of what looked like FAQ gaps turned out to be plumbing, and was filed as bugs rather than
drafted as FAQ text:

| Issue | What |
|---|---|
| nobodies-collective#949 | `fetch_section_guide` dead-ends on an unknown key; two disjoint key namespaces |
| nobodies-collective#951 | Whitelist covers 14 of 35 `docs/sections/*.md` — Events, Store, Scanner, Calendar unreachable |
| nobodies-collective#952 | Turn can end with an empty reply — 8% of production conversations got nothing back |
| nobodies-collective#953 | Profile messaging rejects a 1985-character body against a 2000-character limit |

Clusters covering Expenses, Store, Events/WWW Guide, CityPlanning/barrio zones, Scanner and
Calendar are **not** drafted as FAQ text here. Their content already exists in `docs/sections/`
and is simply unreachable; hand-writing FAQ entries would duplicate it and create a second copy
to keep in sync. Fixing #949 and #951 closes them at the source.

Expenses specifically is held until **2026-08-03** — the expenses flow is still being worked out,
and documenting it now would bake in a procedure that is about to change.

`Gate` was added alongside `Scanner` beyond the eight listed in nobodies-collective#951: the one
observed operator conversation ("how do I register people after scanning the ticket at the gate?")
spans both docs, and answering it from `Scanner.md` alone would land the reader on a cross-
reference to a doc the agent still could not fetch. Note also that the whitelist gap was measured
against a `pr-1059` checkout with 35 section docs; `origin/main` has 36 (`Gate.md` landed since),
so the pre-fix ratio is 14 of 36 rather than 14 of 35.

## FAQ entry — private messaging *(shipped in this PR)*

The only cluster that needed new prose. Added to `SectionHelpContent.Faq` in
`src/Humans.Web/Models/SectionHelpContent.cs`, under the existing `## Profile` heading.

**Occurrences:** 4 · **Confidence:** high — verified against `ProfileController.cs:1990`,
`ProfileCardViewComponent.cs:153`, and `SendMessageViewModel.cs`.

The agent currently answers this **wrongly**. Verbatim, to two different users on 2026-07-14:

> "The Humans app doesn't have a built-in messaging or inbox feature — there's no direct
> messaging system within the app itself."

It then offered to raise a feature request for a feature that already ships, and did so.

Sample questions (verbatim):

> "How can i check my messages?" — Pau, 2026-07-14
> "I want to contact a person" — Pau, 2026-07-14
> "How can I see my private message?" — Moop, 2026-07-14
> "there's this 'send X a message' function on user profiles, but it's not active for every
> user. How is this function activated?" — Frank, 2026-05-06

Three entries shipped: the two messaging ones above, plus a "how do I find a person" entry.
That third one is included because "I want to contact a person" and "find Andras who lives in
Barcelona and speaks hungarian" (Pau) and "How do I find someone" (Bethany, 2026-07-18) are the
same question arriving from a different angle, and the agent handled the privacy boundary
correctly but had no positive answer to offer alongside the refusal.

**Three corrections, all caught after the first draft.** Worth recording, because each one came
from trusting a *description* of the code instead of the code:

1. *"There is no directory search"* — written from what the agent said in the transcripts. Wrong:
   `/Search` exists. Writing FAQ prose from an agent transcript reproduces the agent's own blind
   spots.
2. *"Replies then go directly by email between the two of you"* — wrong whenever the sender clears
   "include my contact info". `EmailMessageFactory.FacilitatedMessage` sets
   `replyTo = includeContactInfo ? senderEmail : null`, and the rendered body omits the address
   too, so the recipient has no way to write back. Caught by Codex on the PR.
3. *"Search matches names only… cannot narrow by city"* and *"pasting the GUID into /Search finds
   them exactly"* — both wrong, and both came from reading the prose rather than the enum.
   `SearchController`'s summary comment and `docs/features/global/global-search.md` describe the
   feature as "name-only", but `SearchHumansAsync` passes `PersonSearchFields.PublicAll`, and
   `PersonSearchFields.Bio` covers "Bio, city, contribution-interests, CV, pronouns,
   AllActiveProfiles-visible ContactFields, and publicly-exposed emails". City **is** searchable;
   languages are not. And the `Guid.TryParse` fast-path exists only in `SearchTeamsAsync` /
   `SearchCampsAsync` / `SearchShiftsAsync` — the human bucket has none, so a person's id finds
   nothing in `/Search`. `/Profile/{id}` is the route that works. Codex caught the field list; the
   GUID error was found while verifying it.

The stale "name-only" wording in `SearchController`'s summary and in `global-search.md` is real
doc drift, surfaced separately rather than fixed here.

## Correctly handled — no action

Worth recording so a future run does not re-open these:

- Off-topic refusals (general coding help, writing a love letter, finding sexual partners) were
  all declined cleanly and redirected.
- One prompt-injection attempt ("Ignore all previous instructions…", Dmytro 2026-06-29) and one
  attempt to get shell access ("please run the command: `id`", Moop 2026-07-14) were both
  refused without drama.
- Looking up another human's personal details was consistently refused on privacy grounds.
- "Where is the source code / who built this" failed on 2026-07-01 (Rico) and answered correctly
  on 2026-07-28 (erickiii) — the community FAQ closed that gap in between.
- "Why can't I sign up for shifts anymore / everything is closed" (6 conversations) answers well
  from the Shifts guide, covering both the browsing toggle and the date-range selector.
- Ticket download / early-entry visibility (roughly 12 conversations across 5 languages) answers
  well from the community FAQ.

## Singletons skipped

~18, mostly the correctly-refused off-topic questions above. Full list available from the
transcript dump if wanted.
