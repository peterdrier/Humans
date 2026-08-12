---
name: Pagination helpers — shared utilities
description: Use `ClampPageSize()` for page-size clamping. No inline `Math.Clamp`. (CSV moved to its own rule: csv-use-csvhelper.)
---

Small repeated mechanics should use the shared helpers once they exist.

**Rule:**
- Use `ClampPageSize()` for repeated page-size clamping instead of scattering `Math.Clamp(pageSize, ...)`
- CSV generation/parsing has its own rule: [`csv-use-csvhelper`](csv-use-csvhelper.md) — the old `AppendCsvRow`/`ToCsvField` helpers are gone.

**Why:** These helpers reduce noise and prevent small formatting/validation differences between endpoints.
