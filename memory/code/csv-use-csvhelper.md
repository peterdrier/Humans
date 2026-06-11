---
name: CSV — CsvHelper via HumansCsv, never hand-rolled
description: ALL CSV reading and writing goes through the CsvHelper package using the shared `HumansCsv` config factories. Hand-rolled splitting, quoting, escaping, or `string.Split(',')` parsing is not allowed.
---

Every CSV read or write uses **CsvHelper** built from the shared factories in `Humans.Application.Csv.HumansCsv`:

- **Writing:** `HumansCsv.WriteBytes(csv => ...)` (+ `csv.WriteRow(...)` for loose values, `WriteRecords`/ClassMap for typed rows). Gives UTF-8 BOM, CRLF, invariant culture, RFC 4180 conditional quoting, and OWASP CSV-injection escaping (`= + - @ \t \r` → leading apostrophe) on every export.
- **Reading:** `HumansCsv.ReadConfig()` + `CsvReader`. Gives delimiter detection (Spanish Excel saves semicolons), case/whitespace-forgiving header-name matching, trimmed fields, quoted multiline fields.
- **Round-trip files** (download → user edits → upload, e.g. the bulk-event template) share one DTO + `ClassMap` between writer and parser so columns can't drift, and turn **off** injection escaping on the write side (`InjectionOptions.None`) — escaping would come back as data and dirty untouched rows.

**Why:** The app previously had three hand-rolled CSV implementations with divergent quoting policies; 5 of 6 exports had no injection escaping, and the bulk-upload parser broke on quoted newlines and semicolon-delimited files. One library + one config makes every surface consistent and keeps the security properties in one place.

**How to apply:** Never write a manual CSV splitter, quoter, or escaper — no `Split(',')`, no `Replace("\"", "\"\"")`, no hand-typed header strings next to positional field indexes. If a call site needs a config tweak, mutate the instance returned by the factory (e.g. `AllowComments`) at the call site. The only allowed package is exactly `CsvHelper` (beware the malicious look-alike `CsvHelper.Excel.Core.Net`). Conventions are pinned by `Humans.Application.Tests/Csv/HumansCsvTests.cs`.
