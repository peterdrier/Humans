---
name: Discord release notes format
description: Audience-grouped (coordinators / volunteers / under-the-hood / known issues), plain-language, no emojis. Use whenever asked for Discord release notes.
---

Structure for Discord release notes when promoting a batch PR to production:

1. **Title line** — "Humans production release — YYYY-MM-DD"
2. **For coordinators** — bolded bullet list of changes that affect coordinator workflows (new toggles, admin pages, permissions)
3. **For volunteers** — bolded bullet list of changes visible to regular volunteers (UI fixes, dashboard tweaks, new features they'll notice)
4. **Under the hood (if you care)** — short section covering meaningful internal changes in plain language (refactors that prevent future bugs, DI/startup changes, migrations). Frame it as "why this matters to you indirectly," not as a changelog.
5. **Known tracked issues (not blockers)** — deferred follow-ups with issue numbers, so the community knows they were considered and triaged rather than missed.
6. **PR link at the bottom**

**Rules:**
- **No emojis.** Plain markdown only.
- **Plain-language tone**, slightly conversational ("if you care", "no more dead buttons", "that's it").
- **Skip pure internal refactors** (service ownership plumbing, log level tweaks, NuGet bumps, tests) unless they have a perceptible effect.
- **Group by audience, not by commit.** A reader should be able to skim the section that applies to them and ignore the rest.
- **Name the actual user-visible effect**, not the commit title. "Avatar fix on the nav bar" beats "Refactor UserAvatarViewComponent to id-based API".
- **Honor the no-event-name rule** — never include the event name in release notes (legal requirement). See [`no-event-name-nowhere`](../product/no-event-name-nowhere.md).

**Why:** Peter approved this format explicitly on 2026-04-15 for PR #496. The community reads Discord — they want to know what changed without wading through a changelog or trying to parse commit messages.

**How to apply:** Use whenever asked for Discord release notes / release summary / "what should I post to Discord". Start from the PR's commit list, bucket by audience, cut anything a non-technical volunteer wouldn't notice, and keep the "under the hood" section to 2-4 lines max.
