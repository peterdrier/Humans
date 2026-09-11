<!-- freshness:triggers
  src/Sections/Humans.Governance/**
  src/Sections/Humans.Governance.Contracts/**
-->
<!-- freshness:flag-on-change
  Vote lifecycle (Draft → Open → Closed/Cancelled), roster snapshot at open, embargo + audited peek, recorded-ballot model with append-only history, IRV counting steps — review when the AssemblyVote entities, services, or controllers change.
-->

# Assembly Votes

**Status: implemented** (peterdrier/Humans#1649). Signed off by Peter 2026-09-10; the decisions at the end of this document are the contract. Section invariants: [`../Governance.md`](../Governance.md).

Binding votes of the association (Asociados voting on motions during or around a General Assembly), with optional indicative participation by the wider community. This is the remaining scope of nobodies-collective/Humans#86 after nobodies-collective/Humans#1157 shipped the Surveys ranked-choice feature.

Not to be confused with **Board voting** ([board-voting.md](board-voting.md)), which is the Board's internal vote on a tier application, nor with the Surveys **Asociado vote** mode ([../../../Humans.Surveys/Docs/features/ranked-choice-voting.md](../../../Humans.Surveys/Docs/features/ranked-choice-voting.md)), which is a secret ballot. See [Why not Surveys](#why-not-surveys).

## Business context

The association adopts agreements by vote of its Asociados in the General Assembly. The statutes (Art. 8.2) allow electronic or remote voting for adopting agreements provided the member's identity and the validity of the vote are guaranteed, and (Art. 10.7) allow votes cast before the session to be counted with those cast during it. Today those votes happen outside Humans. Humans already knows who the current Asociados are, so it is the natural place to run the vote, notify the electorate, record every ballot, and hand the Secretary the numbers the minutes (acta) require.

Two things distinguish this from a survey: the vote is **binding and part of the permanent record**, and each ballot is **attributable, auditable, and changeable until close**. The design below is a *recorded vote under embargo*, not a secret ballot.

### Legal frame (checked 2026-09-10, not legal advice)

Sources: Ley Orgánica 1/2002 reguladora del Derecho de Asociación; the association's statutes, Spanish original at `nobodies-collective/legal/Estatutos/ESTATUTOS NOBODIES.md` (also published at `/Legal` in Humans). The Reglamento de Régimen Interno the statutes mention (Art. 7.7, Art. 11.2) does not exist in that repo yet.

| Question | Answer | Consequence for the design |
|---|---|---|
| Who may vote? | Statutes Art. 22 grant vote to Asociados; Art. 24 says Colaboradores are not members and may not vote; volunteers are not addressed. Board members are elected from the membership (Art. 11) and are de facto Asociados. | Asociados and Board members cast **official** ballots. Colaborador / community ballots are **indicative** and are never counted in the official result. |
| Is remote / electronic voting allowed? | Statutes Art. 8.2: yes, "salvo que la mayoría de los asistentes manifieste expresamente su oposición", provided identity and vote validity are guaranteed. Art. 10.7: advance votes count with session votes. | Voter identity comes from the Humans login; every ballot is tied to a user and its history is kept. The Assembly can still reject the system on the day, so the Admin **stop** must exist and be cheap. |
| Majority? | Statutes Art. 10.2: simple majority (more for than against). Art. 10.4: statute changes need at least half the Asociados attending and 2/3 of those present in favour. Art. 10.5 / Art. 33: dissolution and disposal of assets have their own threshold. LO 1/2002 Art. 12.d adds Board remuneration to the qualified list. | Per-vote `RequiredMajority` (Simple / TwoThirds). Abstentions are neither for nor against. The attendance condition in Art. 10.4 is a quorum matter (below), not computed here. |
| Ties? | Statutes Art. 10.2: on a tie between opposing options, and only then, the President of the Board (or whoever chairs the Assembly) may cast a deciding vote. Not applicable to Board elections (Art. 11.2). | The system never breaks a tie. It reports the tie and prints the Art. 10.2 rule; the Secretary records the President's casting vote, if used, in the acta. |
| Quorum? | Statutes Art. 9.1: half plus one of members on first call; any number on second call. Quorum is a property of the **assembly's attendance**, not of the ballot. | Humans reports turnout against the roster; it does **not** decide quorum. The Secretary does, in the acta. |
| What must the minutes contain? | Statutes Art. 8.6: summary of deliberations, text of adopted agreements, **numerical** voting results, attendee list. | The results page and export give the numbers and the adopted text. Names per ballot are not required by the statutes. |
| Secret or nominal? | Neither the law nor the statutes require either; the word "secreto" does not appear in the statutes. Spanish doctrine treats the voting method as a matter for the association's internal rules. Board elections follow a method the Reglamento de Régimen Interno is to define (Art. 11.2), and that document has not been written. | The choice is policy. This feature is nominal (recorded). If a secret ballot is ever required for a given decision (Board elections may end up there), that vote runs on the Surveys Asociado-vote mode instead. |
| Publish who voted what? | Not required. Not forbidden. A member's vote is personal data under GDPR; the association needs a basis (legitimate interest in the integrity of its decisions covers Board/Secretary access; publishing to all members is a further disclosure). | Default: after close, every member sees the numbers, roster members also see their **own** ballot; Board and Admin see every ballot (audited). Per-vote `BallotDisclosure` (default off) can additionally show names to all roster members if the Board decides so for that vote. Never beyond the roster. |
| Proxy / delegated vote? | Statutes Art. 10.3: allowed, both parties notify the Board 24h before. | Out of scope for v1. Delegated votes are recorded by the Secretary in the acta, outside Humans. |
| Notice period? | Statutes Art. 8.4: 15 days, by email to all Asociados. | The convocatoria is not this feature. The vote-open email is a ballot notification, not the legal notice. |

## User stories

### US-V1: Board creates a vote

**As a** Board member **I want to** draft a vote with its official text, options and closing time **so that** it is ready to open at the assembly.

- Fields: title and official text per culture (Markdown, sanitized), a required `OfficialCulture` whose text is the binding one (others are labelled as translations), optional information link, `Kind` (YesNo or RankedChoice), options for RankedChoice (stable key + per-culture label, authored order), `RequiredMajority`, `IndicativeAudience` (None / Colaboradores / AllMembers), `BallotDisclosure`, `ClosesAt` (Instant, shown in Europe/Madrid), optional `AssemblyDate` (LocalDate, for the acta).
- YesNo votes have fixed options Yes / No / Abstain. RankedChoice votes have at least two authored options and an Abstain flag on the ballot.
- Draft is editable and deletable. Opening locks everything except `ClosesAt`.
- A draft is rejected unless every posted enum (`Kind`, `RequiredMajority`, `IndicativeAudience`, `BallotDisclosure`) is a defined value, and a ballot POST that carries no choice at all is rejected rather than read as the enum's zero value.
- **Save & translate blanks** machine-fills the cultures the author left empty from the vote's `OfficialCulture`, via `IGoogleTranslationService` (the same assist Surveys authoring uses). Blanks only — authored text in any culture is never overwritten — and draft-only, because translating a motion the electorate is already reading would change what some members see mid-vote. The official-culture text stays the binding one; the fills are an authoring aid the Board reviews before opening. The write is refused when the draft was edited while the translations were being fetched — the round-trip is long enough for the Board to rewrite the motion, and a draft edited into a draft would otherwise be overwritten by the translator's older snapshot.

### US-V2: Admin opens the vote

**As an** Admin **I want to** open a draft vote **so that** the electorate is notified and can vote.

Admin-only for the first votes because they are live tests; once the process has run once or twice, opening moves to `BoardOrAdmin` (a policy constant change, no redesign).

- Opening snapshots the **roster**: every current Asociado and every active Board role holder (official, one row per person, Board members are de facto Asociados whatever their profile tier says) plus the indicative audience (Colaboradores and/or all active Volunteers) as of that instant. The roster is the legal record of who was entitled to vote and the denominator for turnout. It never changes afterwards; members approved, expired, suspended or erased during the vote keep or lose nothing on the roster, only their ability to log in.
- Each roster member is emailed in their preferred language with the title, the closing time, whether their ballot is official or indicative, and a link to the ballot page. Category `System` (operational, never marketing).
- Audit entry `AssemblyVoteOpened` with roster counts.

### US-V3: Member casts and changes a ballot

**As a** roster member **I want to** cast my ballot from my phone, see what I recorded, and change it until the vote closes.

- The ballot page works at phone width without JavaScript: large Yes / No / Abstain buttons; for RankedChoice a rank `<select>` per option (1..N, blanks allowed, no duplicate ranks), the same select-per-row pattern Surveys uses in `_SurveyQuestions.cshtml`. Partial rankings are valid (unranked options are never preferred).
- Submitting shows the recorded ballot, its revision number and timestamp. Every cast or change appends a history row and writes an audit entry (`AssemblyBallotCast` / `AssemblyBallotChanged`) that names the voter and vote but **not the choice**.
- The page shows the member's own ballot history (each revision with its timestamp and content) and the live participation stats (US-V5).
- A ballot from an indicative voter is visibly labelled "indicative, not counted in the official result" on the page, in the confirmation and in the email.
- Every logged-in member sees every vote, Open or Closed, in the list and can open its page: title, official text, link, closing time and the participation stats (US-V5). Only roster members get the ballot form; everyone else sees a "you are not on the roster for this vote" note in its place. Ballot POSTs from non-roster members are rejected.

### US-V4: The vote closes

**As the** association **I want** the vote to close at the announced time or when the Admin stops it **so that** results are fixed and cannot change.

- Closes automatically at `ClosesAt` (a recurring job every minute is unnecessary: the service treats `now >= ClosesAt` as closed on every read and write, and an hourly job stamps `ClosedAt` and writes the audit entry for votes that lapsed without a request). Before `ClosedAt` is stamped, any request that observes `now >= ClosesAt` stamps it first.
- **Stop** (AdminOnly, one click plus confirm) sets `ClosedAt = now`, `ClosedByUserId`, audit `AssemblyVoteStopped`. Ballots after `ClosedAt` are rejected.
- **Extend** (AdminOnly) moves `ClosesAt` later while Open, audited with old and new values. Shortening is not offered; use Stop.
- **Cancel** (AdminOnly) while Open: terminal, ballots retained, no result computed, roster notified by email, audit `AssemblyVoteCancelled` with a required reason. This is the "the Assembly refused electronic voting" exit (statutes Art. 8.2).
- Closed and Cancelled are terminal, enforced at the write and not just at the read: every write to a vote row takes that row's lock and applies only while the row is still in the state its caller read. A stop, cancel or extend built from an Open read is refused once anything else has closed the vote — including a second stop, which is how two admins clicking at once produce one close, one audit entry and one notification. An automatic close is refused as well when an Extend has pushed the deadline past the one it read, and an extension is refused when the persisted deadline is already later than the one it carries — two admins extending at once both validate against the deadline they read, and the shorter one must not take the electorate's deadline back. A stop or cancel built from the pre-extension deadline still goes through — it is a legitimate terminal write — but keeps the locked row's later deadline, so the record never shows a deadline the extension superseded. Deleting or opening applies only while the row is still Draft, and opening — like a translation pre-fill — is refused outright when the draft was edited since the write was built, because the electorate is computed from the draft and then frozen. The caller reports the refusal as `WrongState`. A closed vote can never reopen; to redo, create a new vote.
- On close the closed status is persisted **first** and the result computed and stored (`ResultJson`) **second**: a ballot write takes the vote row's lock and re-reads it, so a submission that arrives while the count is running is refused rather than accepted into a tally that has already been taken. A close interrupted after the status write is finished by the next read — result, notification and audit entry, not just the result — and by the hourly sweep, so an interrupted close is never waiting on somebody to open that one vote. The takeover waits five minutes and then claims the row, because a close that committed its status moments ago is running rather than interrupted: without both, a read landing inside a live close would duplicate its audit entry and its notification. The result is computed once and stored, so it is stable even if counting code changes later. It can be recomputed by Admin only in Debug tooling, and the stored one wins.

### US-V5: Everyone sees participation, nobody sees the tally

**As a** member **I want to** see how the vote is going without seeing how it is going.

While Open, the vote page shows to every logged-in member: roster size (official / indicative), ballots cast (official / indicative, "56 of 112 have voted"), turnout %, number of ballots changed at least once, total revisions, time of the last ballot, closing time. Nothing derived from ballot content is available anywhere: no per-option counts, no partial IRV rounds, no export, no Backdoor read.

### US-V6: Admin peeks, and it shows

**As an** Admin **I want to** see the live tally in an emergency **so that** I can make a call at the assembly, and **as a** member **I want to** know when that happened.

- `Peek` (AdminOnly) renders the current tally exactly as the results page would. Every peek writes audit `AssemblyVotePeeked` and a `vote_peeks` row (who, when), and only while the vote is still Open — a vote that closes mid-request makes it an ordinary results read, and the peek list published on that page must not accuse an Admin of an early look.
- The results page after close lists every peek (who, when). Members can see whether anyone looked early.

### US-V7: Results

**As a** member **I want to** see the outcome and the counting steps.

Closed results are visible to every logged-in member, roster or not; Volunteers who could not vote still see what the association decided.

- YesNo: for / against / abstain counts, turnout, `RequiredMajority`, and the verdict (Passed / Failed / Tie). A tie is not resolved by the system: the page states that under statutes Art. 10.2 the President's casting vote decides and the Secretary records it in the acta. Official and indicative tallies are shown as two separate blocks, indicative clearly marked.
- RankedChoice (instant-runoff): a rounds table. Each round shows the first-preference count per continuing option, the count of exhausted ballots, and the option eliminated. An option wins when it holds more than 50% of the continuing (non-exhausted) ballots in that round. Elimination picks the fewest votes; ties for elimination break by fewest votes in the previous round, then by authored option order (disclosed on the page). A final-round tie is reported as a tie with no winner, with the same Art. 10.2 note: the President's casting vote decides, recorded in the acta. With around 120 Asociados this is unlikely, so no in-system resolution is built.
- Abstain ballots count toward turnout and are shown, but are not continuing ballots.
- The results page includes the official text as adopted and an **acta block**: a plain-text summary (title, assembly date, roster size, ballots, result per option — named by its authored label in the vote's official culture, never its storage key — verdict, method, closing time in Europe/Madrid like every other closing time the electorate saw, who closed it) the Secretary pastes into the minutes. A CSV of the rounds/tallies is downloadable. No PDF.
- Individual ballots after close: Board and Admin can list every ballot (name, current choice, revision count) at `/Governance/Votes/Admin/{id}/Ballots`; each view writes audit `AssemblyBallotsViewed`. If `BallotDisclosure = RosterSeesNames` (default off), the same list (without history) is on the results page for roster members only; other members still see numbers only.

### US-V8: The Board can see what happened

Every state change and every ballot event is in the audit log (crosscut). Automation (auto-close, emails) leaves entries under the job actor: the lapse close writes `AssemblyVoteClosed`, each T-24h reminder batch writes `AssemblyVoteRemindersSent` with the number actually delivered, and a retried opening email writes `AssemblyVoteOpened` with the number of members the first send missed — the Admin's own open entry is about their transition and cannot show that the job reached members hours later. Eligibility is re-read per recipient as the batch goes out — the roster row's ballot, the vote's own status and its current deadline — so nobody is told they have not voted after they have, and a stop, cancel or extend mid-batch ends it instead of mailing the rest a deadline the vote no longer has (an extended vote is reminded again once the new deadline comes into range).

## Why not Surveys

Daniel's Surveys section already carries an **Asociado vote** mode with ranked-choice counting (nobodies-collective/Humans#1151, #1157, #1585). It was the right tool for the 2027 event-date decision and stays. It is the wrong base for binding association votes, for reasons that are structural, not cosmetic:

1. **Opposite privacy contract.** Surveys' load-bearing invariant is *unlinkability*: Asociado ballots store no `UserId`, no completion timestamp, and "individual response submissions cannot be audit-logged". Binding votes require the reverse: every ballot attributable, every change audited, the member able to see their own history, and the Board able to inspect ballots after close. You cannot hold "never link" and "always link and audit" as invariants of one section; whichever branch is the exception becomes an untested path through the other's privacy guarantees.
2. **Changeable ballots.** Surveys' CompletionTracked tier cannot resume or amend because there is no link to resume from. Statutes-compliant remote voting during a live assembly needs "change your vote until the chair closes it".
3. **A different lifecycle.** A survey is Draft/Open/Closed with an audience diff and reminders. A binding vote has a roster snapshot at open, an admin stop, extend, cancel-with-reason, a stored immutable result, peeks that are themselves public, and a permanent-record export. None of that has a home in a questionnaire engine's state machine.
4. **A different counting method.** Surveys precommitted to Ranked Pairs with equal ranks and rejection tiers. The association's votes need instant-runoff with visible elimination rounds, which is what the members were told and can follow by hand. Adding a second method with different ballot semantics to Surveys widens a surface that was carefully narrowed.
5. **Ownership.** Eligibility, tiers, terms and the Asociado roster are Governance's data. A Surveys vote checks eligibility through `IUserServiceRead` at each request; a binding vote needs a frozen roster derived from Governance's own tables, which Surveys cannot own without reaching across the boundary.
6. **Scale of the thing.** A binding vote is one question. Surveys brings pages, branching, grids, three anonymity tiers, invite tokens, public slugs, and translation tooling. The Governance implementation is a few hundred lines of service code plus two mobile pages.

What *is* reusable from Surveys: the select-per-option ranked ballot markup (`Views/Shared/_SurveyQuestions.cshtml`, `.survey-ranked-choice`) and the sanitized-Markdown renderer. The renderer is already shared; the ballot markup is view code, so a copy in Governance is fine (implementer's call).

## Data model

All tables in `GovernanceDbContext`, one migration, prefix `assembly_`.

### AssemblyVote

**Table:** `assembly_votes`

| Property | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| Title | jsonb culture → text | max 200 chars per culture: it is copied into the email subject and the notification title, both bounded columns written after an irreversible transition |
| OfficialText | jsonb culture → text | Markdown; rendered through the shared sanitizer |
| OfficialCulture | string(10) | the culture whose text is binding; others are translations |
| InfoUrl | string? (2000) | |
| Kind | AssemblyVoteKind | string-converted: YesNo / RankedChoice |
| RequiredMajority | RequiredMajority | string-converted: Simple / TwoThirds |
| IndicativeAudience | IndicativeAudience | string-converted: None / Colaboradores / AllMembers |
| BallotDisclosure | BallotDisclosure | string-converted: BoardOnly / RosterSeesNames |
| Status | AssemblyVoteStatus | string-converted: Draft / Open / Closed / Cancelled |
| AssemblyDate | LocalDate? | for the acta |
| ClosesAt | Instant | announced close; extendable while Open |
| OpenedAt / OpenedByUserId | Instant? / Guid? | |
| ClosedAt / ClosedByUserId | Instant? / Guid? | `ClosedByUserId` null when auto-closed |
| CancelReason | string? (4000) | |
| ResultJson | jsonb? | the stored result computed at close (`AssemblyVoteResult`) |
| CreatedByUserId / CreatedAt / UpdatedAt | Guid / Instant / Instant | |

**Indexes:** `Status`; `(Status, ClosesAt)` for the lapse sweep.

### AssemblyVoteOption

**Table:** `assembly_vote_options` (aggregate-local, Cascade). RankedChoice only.

| Property | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| VoteId | Guid | FK |
| Order | int | authored order; the disclosed final tie-break, and the column order of the rounds table on the results page, the peek and the CSV |
| Key | string(100) | stable key stored in ballots; length and uniqueness within the vote are service validation, never a DB unique index (`unique-constraints-ids-only`) |
| Label | jsonb culture → text | |

### AssemblyVoteRoster

**Table:** `assembly_vote_roster`. Written once at open, never updated except by erasure.

| Property | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| VoteId | Guid | FK |
| UserId | Guid? | bare FK → User, no nav; null after Art. 17 erasure |
| Tier | MembershipTier | profile tier at open |
| IsBoardMember | bool | held the Board role at open |
| IsOfficial | bool | Asociado or Board member at open |
| NotifiedAt | Instant? | open email queued |
| ReminderSentAt | Instant? | T-24h reminder queued; idempotency anchor |

**Index:** unique `(VoteId, UserId)` filtered to non-null.

### AssemblyBallot

**Table:** `assembly_ballots`. The member's current standing ballot.

| Property | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| VoteId | Guid | FK |
| RosterId | Guid | FK → roster row (intra-section) |
| Choice | AssemblyBallotChoice | string-converted: Yes / No / Abstain / Ranked |
| Ranking | jsonb string[]? | ordered option keys, RankedChoice only |
| Revision | int | 1 on first cast, +1 per change |
| CastAt | Instant | first cast |
| UpdatedAt | Instant | last change |

**Index:** unique `(VoteId, RosterId)`.

### AssemblyBallotHistory

**Table:** `assembly_ballot_history`. Append-only (repository exposes Add and Get only, design-rules §12).

| Property | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| BallotId | Guid | FK |
| Revision | int | |
| Choice / Ranking | as above | the content recorded at this revision |
| RecordedAt | Instant | |

### AssemblyVotePeek

**Table:** `assembly_vote_peeks`. Append-only.

| Property | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| VoteId | Guid | FK |
| AdminUserId | Guid | bare FK, no nav |
| PeekedAt | Instant | |

### Stored result

`ResultJson` holds an `AssemblyVoteResult` record: per-audience (official, indicative) tallies; for YesNo the for/against/abstain counts and verdict; for RankedChoice the list of rounds (`{ counts: {key: n}, exhausted: n, eliminated: key? , winner: key? }`), the tie-break notes, and the winner or `Tie`. Turnout numbers and the peek list are derived at render time from the tables, not stored.

### Enums

| Enum | Values |
|---|---|
| AssemblyVoteKind | YesNo, RankedChoice |
| AssemblyVoteStatus | Draft, Open, Closed, Cancelled |
| RequiredMajority | Simple, TwoThirds |
| IndicativeAudience | None, Colaboradores, AllMembers |
| BallotDisclosure | BoardOnly, RosterSeesNames |
| AssemblyBallotChoice | Yes, No, Abstain, Ranked |

Culture → text dictionaries: Governance owns its own `internal` value object (same shape as Surveys' `LocalizedText`: culture → text, jsonb). Decided 2026-09-10: not shared with Surveys, no promotion to Base.

## Workflow

```
Draft ──open (Admin)──▶ Open ──ClosesAt reached / Stop (Admin)──▶ Closed
  │                      │
  └─delete (Board)       └─cancel (Admin, reason)──▶ Cancelled
```

- Draft: Board/Admin edit or delete. No roster, no ballots, and invisible to members.
- Open: roster frozen, content locked, ballots accepted from roster members, `ClosesAt` extendable, stats visible, tally embargoed except audited peek.
- Closed: result computed and stored once; ballots read-only; results visible per `BallotDisclosure`.
- Cancelled: ballots retained for the record, no result, roster emailed. Not disclosable — the Board's per-ballot list is for Closed votes only. The results page 404s for a cancelled vote (results exist only for a Closed vote with a stored result); the cancellation and its reason are shown on the vote's own page.

## Routing

All member-facing routes are localized (six cultures). Admin routes are exempt.

| Route | Policy | Purpose |
|---|---|---|
| `GET /Governance/Votes` | authenticated | every opened vote (drafts excluded), Open first |
| `GET /Governance/Votes/{id}` | authenticated | text, link, stats for everyone; ballot form + own history for roster members (mobile-first) |
| `POST /Governance/Votes/{id}/Ballot` | roster member, vote Open | cast or change |
| `GET /Governance/Votes/{id}/Results` | any authenticated member, vote Closed | results, rounds, acta block, peek list; own ballot for roster members; names only per `BallotDisclosure` |
| `GET /Governance/Votes/{id}/Results.csv` | same as results | tallies / rounds export |
| `GET /Governance/Votes/Admin` | BoardOrAdmin | all votes, all states |
| `GET/POST /Governance/Votes/Admin/Create`, `.../Admin/{id}/Edit` | BoardOrAdmin | draft authoring |
| `POST /Governance/Votes/Admin/{id}/Delete` | BoardOrAdmin | draft only |
| `POST /Governance/Votes/Admin/{id}/Open` | AdminOnly (BoardOrAdmin later) | snapshot roster, email, audit |
| `POST /Governance/Votes/Admin/{id}/Stop` | AdminOnly | close now |
| `POST /Governance/Votes/Admin/{id}/Extend` | AdminOnly | later `ClosesAt` |
| `POST /Governance/Votes/Admin/{id}/Cancel` | AdminOnly | with reason |
| `GET /Governance/Votes/Admin/{id}/Peek` | AdminOnly | live tally, audited |
| `GET /Governance/Votes/Admin/{id}/Ballots` | BoardOrAdmin, vote Closed | per-member ballots, audited |

Navigation: a `SectionNav` entry ("Votes", `Nav_*` SharedResource key, visible to every member); an open vote adds a `ThingsToDo` entry ("Cast your vote", done when a ballot exists) and a member-dashboard card via the existing Governance seams (`SectionThingsToDo`, `SectionMemberDashboard`); admin entry under Governance in `SectionAdminNav`.

## Actors & roles

| Actor | Can | Cannot |
|---|---|---|
| Roster member (official) | see the vote, cast/change until close, see own history, see stats, see results after close | see others' ballots unless `RosterSeesNames`; see tally before close |
| Roster member (indicative) | same, ballot labelled indicative | count in the official result |
| Non-roster member | see every vote, its text and live stats, and closed results (numbers only) | cast a ballot; see any individual ballot |
| Board | draft, edit, delete drafts, see all votes and stats, see every ballot after close (audited) | open, stop, extend, cancel, peek |
| Admin | everything Board can, plus open, stop, extend, cancel, peek | see the tally before close without an audit trail |
| Automation (lapse job) | close a lapsed vote and audit it, send the T-24h reminder and audit the batch | anything else |

Board members are de facto Asociados: they are on the official roster whether or not their profile tier says Asociado, with one ballot like anyone else.

## Invariants

- Only roster rows with `IsOfficial = true` contribute to the official result; indicative ballots are tallied separately and never merged.
- The roster is written exactly once, at open, from Governance's own `applications` (active Approved Asociado / Colaborador terms), the active Board role holders via `IRoleAssignmentService.GetActiveUserIdsInRoleAsync` (official), and the Volunteers team for `AllMembers`; it is never recomputed. A person appearing in more than one source gets one row, official if any source is official.
- A ballot can be cast or changed only while `Status = Open` and `now < ClosesAt`, by the roster member it belongs to. One ballot per roster row; changes bump `Revision` and append history; nothing is ever deleted.
- Content (`Title`, `OfficialText`, options, `Kind`, `RequiredMajority`, audiences, disclosure) is immutable once Open.
- No read path returns per-option counts, rankings, or any ballot content for an Open vote except `Peek`, and `Peek` always writes its audit entry and peek row in the same unit of work as the read.
- The audit entry for a ballot event names actor and vote, never the choice. The choice lives only in `assembly_ballots` / `assembly_ballot_history`.
- The result is computed once, at close, and stored; the results page renders the stored result.
- Closed and Cancelled are terminal, enforced at the write and not just at the read: every write to a vote row takes that row's lock and applies only while the row is still in the state its caller read. A stop, cancel or extend built from an Open read is refused once anything else has closed the vote — including a second stop, which is how two admins clicking at once produce one close, one audit entry and one notification. An automatic close is refused as well when an Extend has pushed the deadline past the one it read, and an extension is refused when the persisted deadline is already later than the one it carries — two admins extending at once both validate against the deadline they read, and the shorter one must not take the electorate's deadline back. A stop or cancel built from the pre-extension deadline still goes through — it is a legitimate terminal write — but keeps the locked row's later deadline, so the record never shows a deadline the extension superseded. Deleting or opening applies only while the row is still Draft, and opening — like a translation pre-fill — is refused outright when the draft was edited since the write was built, because the electorate is computed from the draft and then frozen. The caller reports the refusal as `WrongState`.
- A vote whose `ClosesAt` has passed is Closed for every purpose, whether or not the lapse job has run yet.
- YesNo verdict: Simple passes when `Yes > No`, fails when `Yes < No`, and is `Tie` when equal; TwoThirds passes when `Yes >= ceil(2/3 × (Yes + No))`. Abstain is excluded from both. The system never applies the President's casting vote.
- IRV majority base per round is the number of ballots that still rank a continuing option; ballots that rank none (or Abstain) are exhausted.
- `TwoThirds` is a YesNo threshold only. A RankedChoice draft carrying it is rejected: instant runoff has no defined 2/3 rule, and accepting the pair would store an acta claiming a threshold the count never applied. The statutes' qualified majorities are asked as YesNo questions; a ranked vote is an election.

## Negative access rules

- Non-roster users **cannot** cast or change a ballot, and **cannot** see any individual ballot.
- Board **cannot** open, stop, extend, cancel or peek (AdminOnly).
- Nobody **cannot**-bypass the embargo: no export, no Backdoor endpoint, no Debug page returns ballot content for an Open vote.
- Indicative ballots **cannot** appear in the official tally, the acta block, or the verdict.
- Nothing **cannot** reopen a Closed or Cancelled vote.
- The lapse job **cannot** touch a repository directly; it calls the service like every other job in this section.

## Triggers

- **Open:** roster snapshot — the deadline is checked again once the roster is built, since building it reads three other sections and a vote that opens already lapsed is open for no time at all; one in-app notification to the roster via `INotificationEmitter.SendAsync` with `sourceKey = vote id`, emitted before the emails and only while the row still reads Open — a terminal transition mid-send resolves by source key, and a row that does not exist yet cannot be resolved; then one email per roster member (`IEmailMessageFactory.AssemblyVoteOpened`, `MessageCategory.System`, preferred language, link to `/Governance/Votes/{id}`), per-recipient try/catch as in Surveys' invite send, each sent row stamped with `NotifiedAt`; audit `AssemblyVoteOpened` (official/indicative counts).
- **Ballot cast/changed:** history row; audit `AssemblyBallotCast` / `AssemblyBallotChanged` (actor = member, entity = the vote, no choice — the ballot id never appears, so an erased member’s retained audit row cannot be joined back to their choice).
- **Stop / Extend / Cancel:** audit with before/after or reason. Cancel emails the roster.
- **After any transition:** the side effects that follow a committed state change (roster email, in-app notification, notification resolve) are best-effort — a failure is logged and the transition stands, since Open/Cancel/Close are irreversible. The one exception is the vote-opened email: an unstamped `NotifiedAt` is a roster member who does not know the vote exists, so the hourly sweep re-sends it to the rows the send never reached, and audits the batch under the job actor. The retry runs while the vote is Open only: "this vote is open, go and vote" is wrong once it has closed.
- The open-vote notification is actionable and stands until the vote closes or is cancelled; it is not retracted for one voter when they cast their ballot. `INotificationAutoResolve` has no per-user-and-key resolve, and the user-wide one would clear the member's prompts for every other open vote of the same assembly. Recorded in the central debt ledger.
- **Close (any path):** stamps `ClosedAt`, computes and stores the result, resolves the open-vote notification via `INotificationAutoResolve.ResolveBySourceKeyAsync`, audits `AssemblyVoteClosed` / `AssemblyVoteStopped` (job actor via the `jobName` overload of `IAuditLogService.LogAsync` when the hourly `governance-assembly-vote-lapse` job in `SectionJobs` does it). Any earlier request that sees the deadline passed closes inline before serving.
- **Peek:** peek row + audit `AssemblyVotePeeked`, on an open vote only. A peek request that arrives once the vote is closed writes nothing and redirects to the ordinary results page, so the page's "this peek has been recorded" notice is never shown over an unlogged read.
- **Ballots list (post-close):** audit `AssemblyBallotsViewed`.
- **Reminder:** 24h before `ClosesAt`, the clock and the vote both re-read per recipient, one email to roster members with no ballot (`IEmailMessageFactory.AssemblyVoteReminder`), stamped on the roster row (`ReminderSentAt`) so it never repeats; sent by the same hourly job as the lapse sweep. Decided 2026-09-10: keep.
- **GDPR export:** `IUserDataContributor` contributes the member's roster rows, current ballots and history under a new `GdprExportSections.AssemblyVotes`. Actor-side data (drafted/opened/closed votes, peeks) goes in a second slice, `GdprExportSections.AssemblyVoteActions`, and is declared as retained: the acta names the closer and the results page publishes the early-view list.
- **Art. 17 erasure:** roster `UserId` → null (tombstone keeps counts and the stored result valid); ballot and history rows are retained unlinked, because the vote is a legal record of the association (Art. 17(3)(b) / (e)). Declared as partial retention in the section's `ErasureDeclaration`.
- **Account merge (`IUserMerge`):** roster and ballots re-FK from source to target; if both accounts are on the same roster, the target's row wins and the source's row goes. Its ballot moves onto the surviving row when that row holds none — the human voted once, through one of their two accounts, and dropping it would take their only ballot out of the binding tally — and is destroyed with the row only when both accounts voted. Either outcome gets an audit entry naming the ballot — but its entitlement is folded in, not dropped: official beats indicative and the Board flag sticks, the same "official if any source made them official" rule the roster snapshot itself follows. The vote's own actor columns (`CreatedByUserId`, `OpenedByUserId`, `ClosedByUserId`) and `assembly_vote_peeks.AdminUserId` move too, so the acta and the published peek list keep naming the surviving human instead of a tombstone.

## Cross-section dependencies

- **Users:** `IUserServiceRead` for names, preferred language, active state; `IUserEmailService` for the notification address.
- **Auth:** `IRoleAssignmentService.GetActiveUserIdsInRoleAsync(RoleNames.Board)` for the Board members on the official roster (already a Governance dependency).
- **Teams:** `ITeamServiceRead` for the Volunteers team membership (`AllMembers` indicative audience).
- **Email:** `IEmailService.SendAsync` + three new `IEmailMessageFactory` messages (opened, reminder, cancelled).
- **Notifications:** `INotificationEmitter` + `INotificationAutoResolve`; one new `NotificationSource.AssemblyVoteOpened`.
- **Audit:** `IAuditLogService.LogAsync` with the new `AuditAction` values listed above (appended at the end of the enum; entity-type strings pinned as literals in Governance's `AuditEntityTypes`).
- **GDPR:** `IUserDataContributor`, `ErasureDeclaration`.
- Nothing new on `Humans.Governance.Contracts`. No other section needs to read votes.

## Mobile

The ballot page is the one surface that must be excellent on a phone: single column, tap targets ≥ 44px, the official text collapsible above the ballot, the current recorded ballot pinned under the form, no JS required to submit. Admin, results and stats pages are desktop-first and only need to not break at phone width.

## Out of scope (v1)

Proxy/delegated votes; quorum determination; secret ballots (use Surveys Asociado-vote mode); board elections (method set by the Reglamento de Régimen Interno, unknown); linking a vote to an Assembly entity; PDF minutes; recounts with options removed; Backdoor API reads.

## Decisions (Peter, 2026-09-10)

1. **Open** is Admin-only for the first votes (live testing); moves to BoardOrAdmin afterwards.
2. **Closed results** are visible to every logged-in member.
3. **`BallotDisclosure`** defaults to BoardOnly; switchable per vote.
4. **T-24h reminder** stays.
5. **No shared localized-text type** with Surveys; Governance owns its own.
6. **Statutes** reviewed from `nobodies-collective/legal`; no secrecy rule; the Reglamento de Régimen Interno does not exist yet.
7. **Ties** are reported, never resolved in-system; statutes Art. 10.2 gives the President the casting vote, recorded in the acta.
8. **Visibility:** every member sees every vote and its live stats; only the roster can cast.
9. **Board members are de facto Asociados** and sit on the official roster.

Anything not listed is the implementer's call within this spec.

## Implementation checklist (for the implementing session)

- `memory/architecture/governance-scope.md` is updated in this PR to add assembly votes to Governance's scope.
- Update `Docs/Governance.md` (Concepts, Data Model, Routing, Actors, Invariants, Triggers) and `Docs/data-access.md`, `Docs/authorization.md` in the implementation commit.
- Six-culture resx for every member-facing string; admin pages exempt.
- One migration on `GovernanceDbContext`.
- Tests under `tests/Humans.Governance.Tests`: roster snapshot, embargo (no content on any Open read), peek audits, ballot change bumps revision and appends history, official/indicative separation, YesNo verdicts (tie, TwoThirds boundary), IRV rounds incl. exhausted ballots and both tie-break steps, lapse closes without the job, no reopen, erasure tombstone keeps the stored result.

## Related

- [Board voting](board-voting.md), [Membership tiers](membership-tiers.md), [Governance invariants](../Governance.md)
- Surveys secret-ballot mode: [ranked-choice-voting.md](../../../Humans.Surveys/Docs/features/ranked-choice-voting.md)
- nobodies-collective/Humans#86 (original ask), nobodies-collective/Humans#1151 and #1157 (Surveys RCV), peterdrier/Humans#1585 (unlinkable Asociado ballots)
