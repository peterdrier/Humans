---
name: CSV and pagination helpers — shared utilities
description: Use `AppendCsvRow`/`ToCsvField` for CSV escaping and `ClampPageSize()` for page-size clamping. No inline `Math.Clamp` or string interpolation.
---

Small repeated mechanics should use the shared helpers once they exist.

**Rule:**
- Use `AppendCsvRow` / `ToCsvField` for CSV generation instead of inline escaping/string interpolation
- Use `ClampPageSize()` for repeated page-size clamping instead of scattering `Math.Clamp(pageSize, ...)`

**Why:** These helpers reduce noise and prevent small formatting/validation differences between endpoints.
