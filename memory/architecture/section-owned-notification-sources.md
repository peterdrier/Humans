---
name: Section-owned notification sources
description: A section that emits a notification declares the source itself (a `NotificationSourceDefinition` static field on its `<Section>NotificationSources` contributor); never add anything to Humans.Notifications or borrow another section's source.
---

A notification source belongs to the section that emits it. Declare it as a `public static readonly NotificationSourceDefinition` field on the section's `<Section>NotificationSources : NotificationSourceContributor` class, register that class with `services.AddSingleton<INotificationSourceContributor, <Section>NotificationSources>()` in `Section.Register`, and pass the field to `SendAsync` / `SendToRoleAsync` / `ResolveBySource*`.

**Why:** the central `NotificationSource` enum and its category switch made every new section edit Humans.Notifications (the PR #2118 symptom). With the definition carrying its own name, `MessageCategory`, inbox group and retired flag, Notifications holds no list of anyone's values: the registry collects the contributors at startup and fails on a duplicate or over-long name.

**How to apply:**

- The `Name` is stored on every row: never rename one; `tests/Humans.Web.Tests/Infrastructure/NotificationSourceNameStabilityTests.cs` pins the names already stored.
- A source nothing emits or resolves any more gets `Retired: true` and stays declared, so the nightly purge can delete its unresolved rows.
- Do not use another section's definition. If section A must resolve a notice section B sent, the resolve belongs in B's service (Users resolves its own `AccessSuspended` when it restores access; Consent only calls `RestoreConsentSuspensionAsync`).
- Section tests reference the section's own fields; `Humans.Notifications.Tests` uses its own `TestSources`.
- Document the section's sources (class, recipients, trigger) in its `Docs/<Section>.md`; `notification-inbox.md` lists none.
