---
name: Never rename fields on JSON-serialized classes
description: Existing stored JSON expects current property names. Renaming silently breaks deserialization of records already on disk / in DB / in transit.
---

Classes that are JSON-serialized (to databases, APIs, files) will break if properties are renamed. Existing JSON expects the current property names.

**Rule:** Never rename properties on serialized classes. Existing data expects the current property names.

**Why:** Stored records use the property name as the JSON key. After a rename, deserialization can't bind the old key into the new property — that field comes back as the type's default and any data it held is silently lost.

**Example:**
```csharp
// WRONG — breaks existing data
public class User {
    public string UserName { get; set; }  // Renamed from "Name"
}

// CORRECT — keeps existing property name
public class User {
    public string Name { get; set; }  // Matches JSON in storage
}
```

**Exceptions:**
- Adding `[JsonIgnore]` computed properties is safe (they're not serialized)
- Adding new properties is safe (old records will use default values)

**Related:** [`json-serialization`](json-serialization.md) — required attributes when adding new serialized types.
