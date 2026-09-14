---
name: Use `MemberApplication` alias for the Application entity
description: Referencing `Humans.Domain.Entities.Application`? Alias it `using MemberApplication = ...;` — it collides with the framework `Application` type.
---

Due to namespace collision, use the `MemberApplication` alias when referencing `Humans.Domain.Entities.Application`:

```csharp
using MemberApplication = Humans.Domain.Entities.Application;
```

**Why:** `Application` collides with the framework's `System.Windows.Forms.Application` and similar types in some contexts. The alias makes the domain entity unambiguous and removes the need for fully-qualified references at every call site.
