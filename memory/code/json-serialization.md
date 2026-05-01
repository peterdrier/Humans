---
name: JSON serialization (System.Text.Json) — required attributes
description: Private setters need [JsonInclude]; new data classes need [JsonConstructor] (private parameterless); polymorphic types need [JsonPolymorphic] + [JsonDerivedType] on base.
---

The project uses `System.Text.Json`.

**Required attributes:**
- Private setters: `[JsonInclude]`
- New data classes: `[JsonConstructor]` (private parameterless)
- Polymorphic types: `[JsonPolymorphic]` + `[JsonDerivedType]` on base class

**Example:**
```csharp
public class MyData {
    [JsonInclude]
    public string PrivateProp { get; private set; }

    [JsonConstructor]
    private MyData() { }
}
```

**Why:** `System.Text.Json` skips properties without public setters by default unless `[JsonInclude]` opts them in. Polymorphic deserialization needs explicit `[JsonDerivedType]` registrations on the base class to know which concrete type to instantiate.

**Related:** [`no-rename-serialized-fields`](no-rename-serialized-fields.md) — never rename properties on serialized classes.
