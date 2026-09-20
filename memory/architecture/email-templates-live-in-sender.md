---
name: Email templates live in the sending section
description: Adding or editing an email template: build it in the sending section (own resx, own tests, an IEmailPreviewContributor), never in the Email crosscut.
---

# Email templates live in the sending section

A section that sends mail owns its templates: an internal `<Section>Emails` builder
returning a ready `EmailMessage`, its `Email_*` keys in that section's own resx set in all
six cultures, its tests in `tests/Humans.<Section>.Tests`, and an `IEmailPreviewContributor`
registered from `Section.Register` so the template shows in `/Email/EmailPreview`. Email keeps
the mechanics only — `EmailMessage`, `IEmailService.SendAsync`, the outbox, transport, branded
wrapper, unsubscribe, `CultureScope`, and `TimeSensitiveTemplates`.

**Why:** Email is a crosscut, so it must carry no section-specific logic
([`crosscut-purity`](crosscut-purity.md)). Hosting every section's copy made
`IEmailMessageFactory` a 32-method catalogue of other sections' verbs, put 71 `Email_*` keys ×
6 cultures in the crosscut's resx, and forced Email to reference Events, Tickets and Campaigns
contracts — the direction the rule forbids (peterdrier/Humans#1651, design signed off
2026-09-10).

**How to apply:**

- New template: build it in the sending section, render inside
  `using (new CultureScope(culture))` over that section's `IStringLocalizer`, HTML-encode
  substitutions with `WebUtility.HtmlEncode`, and add a sample to that section's
  `IEmailPreviewContributor`. Never add a method to `IEmailRenderer` / `IEmailMessageFactory`,
  and never add an `Email_*` key to `EmailResource.*.resx`.
- Editing a template that has not migrated yet: move it to its section rather than editing it
  in place, unless the change is a same-day fix — the remaining migration is tracked in
  `docs/architecture/debt-ledger.yml`.
- Time-sensitive mail: name the constant on `TimeSensitiveTemplates`; that one list drives both
  the immediate drain and the outbox batch priority. There is no per-message immediate flag.
- The gallery is a fan-out: Email never lists which sections contribute
  ([`fanout-spokes-name-themselves`](fanout-spokes-name-themselves.md)).
- `FacilitatedMessage` is the one template that stays in Email — a generic person-to-person
  relay with no section vocabulary.

**Related:** [[crosscut-purity]], [[fanout-spokes-name-themselves]],
[[resource-key-prefix-matches-section]].
