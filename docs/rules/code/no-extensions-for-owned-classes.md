---
name: No extension methods for classes we own
description: Don't write extension methods on types defined in this repo. Add the method or computed property directly on the class. Extensions are only for types we don't control (BCL, third-party).
---

Don't add extension methods (`public static X Foo(this OwnedType ...)`) for classes the project owns. Add the method or computed property directly on the class itself.

**Why:** Extensions for owned types fragment the surface area — readers have to look in two places to know what a class can do. Properties/methods on the class itself are discoverable via IDE navigation and live next to the data they operate on. Extensions are for types we can't modify (BCL primitives, third-party libs).

**How to apply:**

When proposing a new predicate, helper, or computed value for a class in `Humans.Domain`, `Humans.Application`, etc., put it on the class. Only reach for `static class FooExtensions` when `Foo` is `string`, `IEnumerable<T>`, `HttpContext`, or a NuGet-package type.

**Example:** instead of `public static bool IsPublicForYear(this Camp camp, int year)`, write `public bool IsPublic => Seasons.Any(...)` directly on `Camp` (assuming the seasons collection is already year-scoped by the loader).
