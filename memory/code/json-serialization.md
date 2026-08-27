---
name: JSON serialization (System.Text.Json) — required attributes
description: Private setters need [JsonInclude]; new data classes need [JsonConstructor] (private parameterless); polymorphic types need [JsonPolymorphic] + [JsonDerivedType] on base.
---

The project uses `System.Text.Json`.

**Required attributes:**
- Private setters: `[JsonInclude]`
- New data **classes**: `[JsonConstructor]` (private parameterless)
- New data **records**: `[method: JsonConstructor]` on the record declaration (the `method:` target is the primary constructor; on a parameter it is a compile error)
- Polymorphic types: `[JsonPolymorphic]` + `[JsonDerivedType]` on base class

**Example — a class:**
```csharp
public class MyData {
    [JsonInclude]
    public string PrivateProp { get; private set; }

    [JsonConstructor]
    private MyData() { }
}
```

**Example — a record:**
```csharp
[method: JsonConstructor]
internal sealed record MyMarker(Guid Id, string Culture);
```

**Records are the one place "private parameterless" is the wrong answer.** A positional
record's properties are init-only and are bound *through* its constructor. Give it a private
parameterless constructor and that is the one `System.Text.Json` picks — it builds the object
with `default` for every field and never binds anything, which is the exact failure this rule
exists to prevent. Attribute the primary constructor instead. `System.Text.Json` would in fact
bind a single-public-constructor record with no attribute at all; we mark it anyway so the
serialization contract is visible at the declaration and does not silently change the day a
second constructor is added.

**Why:** `System.Text.Json` skips properties without public setters by default unless `[JsonInclude]` opts them in. Polymorphic deserialization needs explicit `[JsonDerivedType]` registrations on the base class to know which concrete type to instantiate.

**Related:** [`no-rename-serialized-fields`](no-rename-serialized-fields.md) — never rename properties on serialized classes.
