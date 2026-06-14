# Community KB — `## Keywords` section (generator handoff spec)

**Status:** Ready for the KB generator to implement
**Date:** 2026-06-15
**Repo affected:** `nobodies-collective/knowledge-base` (the offline-generated corpus), files under `docs/community-kb/*.md`
**Consumed by:** Humans app — `CommunityFaqReader.ExtractKeywords` (PR #1013)

## Why

The Humans in-app agent routes community questions by reading a small **index** of the KB (one line per topic file) and then fetching the full file on demand. The index line is the topic's `# H1`, its `## Overview` summary, and — added in PR #1013 — a `covers:` keyword list. Without keywords the router can't tell that, say, `lnt.md` answers "are there female urinals this year?" (the words "urinal/VIPee/Octopee" live deep in the file, not in the Overview). The index is server-side prompt-cached, so keywords cost effectively nothing per turn but make coverage legible to the router.

**The app does not derive these keywords itself** — extraction (tokenisation, EN+ES stopwords, proper-noun handling, dedup) is an offline, reviewable concern. The app only reads what the generator declares. This spec defines exactly what to emit.

## What to add

For **every** file under `docs/community-kb/`, add one `## Keywords` section containing a curated, comma-separated list of routing keywords for that topic. Recommended placement: immediately after `## Overview`.

```markdown
## Keywords

toilets, sanitation, TAP, PMS, urinals, vulva urinals, female urinals, VIPee, Octopee,
grey water, recycling, waste sorting, War zone, trash, compost, Punto Limpio, Fraga,
Shit Ninjas, TAP Dancers, urinal adapter, hand-washing, raccoon mascot
```

## Parsing contract (do not break)

The app reads the section like this — match it exactly:

- It finds the line that trims to exactly `## Keywords` (case-insensitive). Use `##`, **not** `###`.
- It reads every following line **until the next `##` heading** (any `##…` ends the section).
- Non-empty lines are trimmed and **joined with a single space**, then rendered verbatim after `covers:`.

Implications:
- Keywords may be on one line or wrapped across several — both work (newlines collapse to spaces). Commas are for human readability; the app does not tokenise.
- Do **not** put another `##` heading inside the keyword block.
- Keep it to one logical list — no sub-bullets, no tables.
- A file with no `## Keywords` section is fine: the index just shows the Overview summary for it (no error).

## Extraction rules

Produce the terms a member would actually type that should land on this topic.

**Include**
- Distinctive nouns and noun phrases for what the topic covers (e.g. `grey water`, `art grants`, `bus tickets`, `early entry`).
- Jargon and acronyms **with their expansion as separate terms**: `EE, early entry` · `TAP, Total Access Pee/Poop/Period` · `LNT, Leave No Trace` · `MoN, Middle of Nowhere` · `NWP, No Water Project` · `LI, low income`.
- Synonyms and colloquialisms people use: `urinals, female urinals, vulva urinals, VIPee, Octopee`.
- Query-worthy proper nouns: facilities, products, places, teams, org names (`War zone, Werkhaus, Cantina, Sariñena, Banc Sabadell, Stripe, TicketTailor, Barrio Speed Dating`).
- Bilingual forms where the community uses both: `estatutos, bylaws` · `barrio, camp` · `IVA, VAT`.

**Exclude**
- Full sentences and FAQ questions — keywords, not prose.
- Bare personal/contributor names (people ask "who leads LNT", which routes via `LNT`/`lead`; a roll-call of names is noise). Keep a name only if it is itself a likely search term (rare).
- URLs, email addresses, dates, and bare numbers.
- Generic stopwords (EN + ES: the/and/of/for/with · el/la/los/de/y/para/con …).

**Shape**
- De-duplicate case-insensitively.
- Lower-case ordinary words; preserve casing of acronyms and proper nouns (`TAP`, `VIPee`, `Werkhaus`).
- Aim for the distinctive terms; **up to ~100 per file** is fine, but do not pad — a tight 30–60 usually beats a noisy 100.

## Ready-to-paste prompt for the generator

> You are updating the community knowledge-base files under `docs/community-kb/*.md`. For **each** file, insert one `## Keywords` section immediately after the `## Overview` section.
>
> The section must contain a single comma-separated list of **routing keywords** — the terms a community member would type that should route a question to this topic. This list is read by an automated index (newlines are collapsed to spaces; commas are for readability only), so:
> - Use the heading `## Keywords` exactly (two hashes), and end the list before the next `##` heading. No sub-headings, bullets, or tables inside it.
> - **Include:** distinctive nouns/phrases for what the file covers; every acronym **and** its spelled-out form as separate terms (e.g. `EE, early entry`); synonyms/colloquialisms (e.g. `urinals, female urinals, vulva urinals, VIPee, Octopee`); query-worthy proper nouns (facilities, products, places, teams, orgs); and bilingual EN/ES forms where both are used (`estatutos, bylaws`).
> - **Exclude:** full sentences and FAQ questions; bare personal names; URLs, emails, dates, and bare numbers; generic English/Spanish stopwords.
> - De-duplicate case-insensitively; lower-case ordinary words but keep acronym/proper-noun casing; up to ~100 terms per file but prefer a tight, high-signal set (don't pad).
>
> Derive the keywords from the whole file — Overview, Key facts, and FAQ — not just the title. Do not modify any other content. Output the edited markdown for each file.

## Verification

After regeneration, the app surfaces these automatically — no app change needed. The admin **Reload KB** button (`POST /Agent/Admin/ReloadKnowledgeBase`) or a restart re-reads the corpus; the community index lines then render `… — covers: <keywords> (last updated …)`. Spot-check that `lnt` covers "urinals/VIPee", `general` covers "early entry/EE" and "event name/Elsewhere", and `tickets` covers "low income/LI".
