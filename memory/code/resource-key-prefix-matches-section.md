---
name: Resource key prefix matches the section name
description: Every new resource key in a section's `<Section>Resource.resx` is prefixed with the section name plus an underscore — `Users_`, `Tickets_`, `Camps_`. Existing keys are not backfilled; new keys follow the rule.
---

**A resource key's prefix is its section's name.** `Humans.Users` → `Users_`, `Humans.Tickets` → `Tickets_`, `Humans.CityPlanning` → `CityPlanning_`. The section name verbatim as the project spells it — **plural, PascalCase** — then `_`, then whatever structure the key needs (`Users_Profile_Title`, `Camps_Index_BarrioGuide`).

Keys are PascalCase in every segment, not lowercase: all 2,618 keys in the tree on 2026-08-20 were, with no exceptions. The underscore is a separator between segments, not a word separator inside one.

The prefix stops being a second, hand-maintained taxonomy. Once a section owns its own resx set, a key's *file* already says which section it belongs to — a prefix that disagrees is a name that has to be looked up instead of read, and a key living in the wrong set no longer announces itself.

**Applies to new keys only.** As of 2026-08-20 about 713 of 1,960 section keys conform; the rest are not being backfilled. Don't rename existing keys as a side effect of unrelated work — a rename touches six language files and every call site, and a missed one renders raw with no error. Backfill a prefix only as its own deliberate change, one section at a time.

Singular section-name variants are hits, not exceptions: `Camp_` in `Humans.Camps`, `Ticket_` in `Humans.Tickets`, `Issue_` in `Humans.Issues`.

`SharedResource` is exempt — it is not a section, and its keys are the cross-section vocabulary (`Common_`, `Nav_`, `Validation_`, `Enum_`). *Which* set a key belongs in is the carve question (`docs/sections/G5-SECTION-TEMPLATE.md` step 3b), not this one; this rule only names the key once the set is settled.

`Enum_{TypeName}_{Value}` keys keep that shape — they are resolved by reflection through `Localizer.EnumDisplay`, so the type name is the prefix and renaming it breaks silently. See [`type-name-as-persisted-string`](type-name-as-persisted-string.md).

Tracked as a cleanup item by `/section-doctor`'s conformance thread (`docs/architecture/section-conformance.yml` → `resource-key-prefix`).
