---
name: Don't remove "unused" properties — they may be reflection-bound
description: Don't remove a seemingly-unused property/method without checking for reflection use — serialization, EF change tracking, cloning/merging, dynamic binding.
---

Properties/methods appearing "unused" may be used dynamically via reflection:
- Serialization / deserialization
- Change tracking
- Object cloning / merging
- Dynamic binding

**Rule:** Do not remove properties/methods that appear unused without verifying they're not used via reflection.

**Why:** "Find references" misses reflection-driven access. Removing a property that's deserialized from stored JSON or scanned by an analyzer silently breaks code paths that won't trip until production data hits them.

**How to apply:** Before deleting an "unused" property, check:
- Is it on a class with `[JsonInclude]` / `[JsonConstructor]` / `[JsonPolymorphic]` attributes? Reflection-bound.
- Is it on an EF entity? Likely scanned by EF's change tracker.
- Is it on a class registered with a framework (DI options, Razor model binding, MVC view model)? Possibly reflection-bound.
- Grep for the property name as a string literal — it may be referenced via `nameof(X)` or as a magic string somewhere.
