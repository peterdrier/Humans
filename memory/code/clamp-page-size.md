---
name: Use ClampPageSize() for page-size clamping
description: Use `ClampPageSize()` for repeated page-size clamping instead of scattering inline `Math.Clamp(pageSize, ...)` calls.
---

Use `ClampPageSize()` for repeated page-size clamping instead of scattering `Math.Clamp(pageSize, ...)` inline at each call site.

**Why:** the shared helper reduces noise and prevents small validation differences between endpoints.

Related: [`csv-use-csvhelper`](csv-use-csvhelper.md) — CSV generation/parsing has its own rule; the old `AppendCsvRow`/`ToCsvField` helpers are gone.
