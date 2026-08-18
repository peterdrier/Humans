---
name: A section is its assembly
description: A section is an assembly declaring `<AssemblyName>.Section : ISection`, and its name is the assembly name minus the `Humans.` prefix and any `.Contracts` suffix. There is no section attribute.
---

# A section is its assembly

`Humans.Store` is section Store. So is `Humans.Store.Contracts`. Membership is the
`Section : ISection` type at the project root — the one boot discovery registers — and the
name is the assembly name with `Humans.` stripped off the front and `.Contracts` off the back.
Boot discovery, controller and view-component routing, the resource-set scan and the analyzers
all read that one type. Nothing declares a section twice.

**Why:** `[Section("Store")]` said the same thing a second time and could disagree with the
project it sat in. It also carried a fold (`Users`/`Profile`/`Profiles` → `Humans`) that only
existed to reconcile attribute values with namespaces, and layer predicates
(`IsApplicationOrWeb` and friends) that re-derived at analysis time what
`src/Directory.Build.props` already decides by attaching the analyzers to `src/` only.
All of it went in nobodies-collective/Humans#1064.

**How to apply:**

- Adding a section: create `Section.cs : ISection` at the project root and reference the project
  from `src/Humans.Web`. That is the whole registration.
- Renaming a section means renaming the assembly. There is no second place to update.
- A `.Contracts` **assembly** exists only to break a reference cycle; a `Contracts/` folder in
  `Humans.<Section>` is the default, because every extra assembly costs a build and a deploy
  many times a day.
- Inside a section, `Contracts/` is the public folder and `Interfaces/` the internal one —
  HUM0034 enforces it.
- Don't write a guardrail that re-derives which assembly it is running in.

**Related:** [`sections-are-logical-units`](sections-are-logical-units.md),
[`section-controllers-need-feature-provider`](section-controllers-need-feature-provider.md),
[`section-read-write-split`](section-read-write-split.md).
