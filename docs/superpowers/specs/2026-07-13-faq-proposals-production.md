# FAQ Proposals — Agent Triage, Production (2026-07-13)

Source: 100 agent conversations (2026-06-03 → 2026-07-12) pulled via `/api/agent/conversations`,
cross-referenced with the in-app Issues backlog. `refusalCount`/`handoffCount` were 0 on every
conversation (counters never populated), so clusters were built from the raw question stream plus
spot-read transcripts.

Proposals only — apply to `src/Humans.Web/Models/SectionHelpContent.cs` by hand (or direct the
skill to open a PR). Cluster #4 is **blocked on Peter confirming the actual EE admin process**.

---

## Cluster 1: How do I download my ticket?

**Section:** Tickets
**Occurrences:** ~9 conversations (Dafne/Paula, Sixto ×2, Ole, Tarek, Teresa, Houyam, Rae of light) + 3 in-app issues (Marie `iss:06eae01b`, Emilie `iss:3ba310de`/`iss:a22808fc`) — asked in ES, FR, DE, EN
**Sample questions:**
> Veo mi entrada en el perfil, pero no puedo descargarla. No se puede? O no es necesario?
> comment télécharger mon ticket ?
> How can I downlad the early entry ticket that appears on my home page ? Can I get somewhere a PDF ?

**Proposed FAQ entry:**

#### How do I download my ticket / get a PDF?

You can't download a ticket from this app — the card on your dashboard is informational, showing
that your ticket is matched to your account (and your early-entry date if you have one). The actual
ticket PDF/QR comes from TicketTailor's confirmation email, sent to the address the ticket was
bought with. Check that inbox (and spam). If you can't find the email, the Gate can look you up by
name or email — being matched in the app is what matters.

**Confidence:** high

---

## Cluster 2: Event logistics — address, directions, gate hours, program

**Section:** cross-section (event info)
**Occurrences:** ~13 (Alain, Jack, Elisa, Maëva ×2, Felanders, Dafne/Paula ×2, Sixto, Martina, Nico, Kalissa, Nora)
**Sample questions:**
> What is the address of Elsewhere ?
> A quelle heure les entrées sont clôturées ?
> Where can I find the full program of the activities during the event?

Today the agent answers these from the **crowd-sourced community FAQ (Discord)** and hedges every
reply as "not official and may be outdated." An official entry upgrades all of these.

**Proposed FAQ entry:**

#### Where is the event, how do I get there, and when do gates open/close?

*(Fill with official values before applying:)* Site location (Monegros desert near Sariñena,
Huesca — official coordinates/link), getting-there guide (https://nobodies.team/getting-…), gate
open/close hours, and where to find the What-Where-When / program. Until this is in the guide, the
agent can only cite the unofficial community FAQ.

**Confidence:** medium — needs official values from the org, but the gap is high-volume.

---

## Cluster 3: Shifts — sign up, bail, expectations

**Section:** Shifts
**Occurrences:** ~11 (Karin, Refrutiado, Ava Raver, Lady Blue, Toni, Playa, Chopo, Simi ×2, Zezinho, Viktor) + 2 in-app issues (Soph `iss:3bfa9016`, Sanchez `iss:415d6b0a`)
**Sample questions:**
> How to bail a shift
> trying to sign up for a shift and I can't
> how many shifts am I expected to volunteer?

The agent handles sign-up/bail mechanics well, but the KB is missing the rules users actually trip on.

**Proposed FAQ entry:**

#### Why can't I sign up for (or bail from) a shift?

Sign-up failures are almost always capacity: each shift has a hard volunteer cap, and early-entry
(build) days also have a per-day cap — there is no limit on how many shifts one person can take.
To withdraw, go to /Shifts/Mine and use Bail/Withdraw. **Exception:** build shifts lock once early
entry closes — after that date you can't bail yourself; contact your team coordinator, who can
remove you.

**Confidence:** high (rules verified against `ShiftSignupService`)

---

## Cluster 4: Early entry — user side and admin side  ⚠️ BLOCKED

**Section:** Tickets / Shifts
**Occurrences:** ~8 (Annelies ×3, Max K, Kat, Ted, Tarek, Danir)
**Sample questions:**
> i am an administrator of early entry tickets. where do i update tickets to have corerct early entry permission
> How can I add an early entry ticket for my artist
> When was the Early Entry Freeze?

The agent told an EE administrator that early entry "isn't something you manage through this app,"
which appears wrong or incomplete (`IEarlyEntryService`, EE allocation and freeze machinery exist).
**Do not draft until Peter confirms the actual admin process** (who grants EE, where in the UI,
what the freeze date means for changes). A wrong FAQ here is worse than none.

**Confidence:** blocked — needs Peter's answer on the EE admin workflow.

---

## Cluster 5: Ticket transfer, resale, name changes

**Section:** Tickets
**Occurrences:** ~7 (Asstupenda, Héloïse ×2, Franci, V, RainAir, Alew) + 3 in-app issues (nunu `iss:c852c576`, dani `iss:ca519cc5`, Basem `iss:9b02226e`)
**Sample questions:**
> Where can i tranfer my ticket?
> i want to change name on a ticket
> Hi my name in my ticket contains only one of my surnames. How can I update it?

**Proposed FAQ entry:**

#### How do I transfer my ticket or fix the name on it?

To give your ticket to someone else, use the in-app transfer at /TicketTransfer — the receiver gets
the ticket reissued to their account. The name shown on your ticket comes from the TicketTailor
purchase (the holder field), not your profile — name corrections happen on the TicketTailor side.
If a friend bought your ticket under their own email, they can transfer it to you in-app.

**Confidence:** high (flow verified against `TicketTransferService`; double-check the /TicketTransfer URL and that the flow is enabled for all users before applying)

---

## Cluster 6: Discord / contact channels

**Section:** cross-section (community)
**Occurrences:** ~6 (Nani, Mango Merlin, Yakir, Double Feel, Joan Jax, Philou)
**Sample questions:**
> whats the discord url?
> can i contact someone for malfare on discord?
> the volunteer coordinator asked me to contact directly with the interpreter department, do you have a contact?

**Proposed FAQ entry:**

#### How do I join the Discord / contact a specific team?

*(Fill with the official invite link and channel map before applying:)* Discord invite URL, which
channels map to which departments (welfare/malfare, interpreters, …), and whether Telegram groups
exist. The agent currently has no channel directory at all.

**Confidence:** medium — needs the official invite link and channel list.

---

## Cluster 7: Teams and roles — what they do, how to join

**Section:** Teams
**Occurrences:** ~8 (NAWFAL, Kiwi, Wazo, Joan Jax, Eva, Toctoc, Danir, Toni)
**Sample questions:**
> Que hacen en production & logistics
> What are the responsibilities of the LNT Lead of a camp?
> i want to register as consent lead of mirage, where can i do that?

**Proposed FAQ entry:**

#### What do the teams do, and how do I take a role like consent lead?

Team pages at /Teams describe each department and list members; join a team from its page. Camp
roles (consent lead, LNT lead, …) are assigned by the camp's manager on the camp page — ask your
camp lead to add you; there is no self-service role signup. *(Verify role-assignment path before
applying.)* Consider adding one-line descriptions for the most-asked departments (production &
logistics was asked twice).

**Confidence:** medium — verify the camp-role assignment flow.

---

## Skipped singletons (~15)

invoice request, .ics export, barrio zone colours (×2 same user), "who is lucy", payment by card,
travel expenses, ticketswap purchase, gate-terminal operator flow, event-submission how-tos (French,
×4 same user), map of attendee origins, welfare check-in idea, sticker/vehicle pass question, and
one inappropriate question (declined appropriately).
