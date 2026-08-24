---
name: name-analyzers-not-numbers
description: Never refer to an analyzer by its HUMxxxx id alone — lead with what the rule enforces, in plain words; the id is trailing detail.
---

Peter does not carry the HUMxxxx analyzer ids in his head. "HUM0002/HUM0019/HUM0014" reads as noise. Every mention of a rule leads with what it enforces, in plain words — the analyzer class name and the id are trailing detail, not the identifier.

Write: "the Identity-column write rule (`IdentityColumnWriteAnalyzer`, HUM0002) — stops app code writing `User.Email` directly." Not: "HUM0002 fired on User.cs".

**Why:** he reviews decisions about rules, not rule numbers; a bare id forces him to look it up before he can judge anything.

**How to apply:** applies to chat, commit messages, PR bodies, and issue text. The same holds for any other opaque local identifier — lane numbers, design-doc section numbers, issue numbers used as a noun. Say the thing, then cite the reference.
