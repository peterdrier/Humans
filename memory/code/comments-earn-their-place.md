---
name: comments-earn-their-place
description: Writing or editing any code comment or XML doc block: a comment says what the code can't (why, a warning, a contract), never what the code does or how it got here.
---

A comment exists for the next person changing this code. Keep it only if it tells them something they need and can't get from the code, names, types, or git. Test: delete it. Would a competent reader new to this file be misled or slowed down? If not, it goes.

**Keep** (one to three lines):
- **Why**: the reason behind a choice that looks wrong or arbitrary: a constraint, legal/GDPR requirement, vendor quirk, RFC.
- **Warnings**: ordering that matters, a call that must not move, a trap the next editor would fall into.
- **Invariants** the code relies on but doesn't check on that line.
- **Contracts on public surface**: a public Contracts member keeps a `<summary>` only when the caller can rely on something the name and signature leave out (null meaning, ordering, caching, side effects). A member whose name and signature say it all gets no summary.

**Cut:**
- **Restating code**: `/// Gets the user by id` on `GetUserByIdAsync`; `<param>`/`<returns>` that repeat the name; a line naming what the next block plainly does.
- **History**: "moved from", "used to", "decomposition commit N", issue/PR numbers explaining how it came to be. Git holds this. Link an issue only for open work the reader must know about.
- **Facts stated elsewhere**: table lists, URL lists, rules already in the section doc, `AGENTS.md`, or enforced by an analyzer.
- **What isn't there**: "deliberately absent", "no navigation properties". The code shows it; analyzers guard it.
- **Boilerplate copied across files.** If it's in 35 files, it belongs in one doc or nowhere.
- **Wrong or stale comments**: fix or delete; a wrong comment can mark a real bug.

**Why:** Peter (2026-10-21): `EmailDbContext`'s 16-line header added nothing: every claim was in the code, in git, repo-wide, or wrong, and the same pattern runs app-wide. The old rule capped length but never asked whether a comment adds value, so short useless comments kept coming.

**How to apply:** when writing a comment, apply the test before typing it. When editing near an existing comment that fails it, shorten or delete it rather than adding to it. Same for `because:` strings in test assertions. Generated files (migrations) are never edited. A record or method that documents some `<param>`s must document all of them (CS1573 fails the build): when only a few need a note, drop the `<param>` tags and put the notes in `<remarks>` with `<paramref>`. A doc with only `<remarks>` fails RCS1139: use `<summary>`. Some browser tests locate script code by a comment line (`indexOf('// Chip toggle')`): grep `tests/` for a JS comment's text before cutting it.

Related: [[name-analyzers-not-numbers]].
