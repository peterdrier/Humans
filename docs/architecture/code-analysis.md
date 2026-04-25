# Code Analysis

## Roslyn Analyzers (Build-time)

Runs during `dotnet build`:
- **Meziantou.Analyzer** (MA0xxx) - Code quality
- **Roslynator.Analyzers** (RCS0xxx) - C# analysis
- **Microsoft.VisualStudio.Threading.Analyzers** - Async/threading

**Common suppressions in `Directory.Build.props`:**
- `MA0048` - File name must match type name
- `MA0016` - Prefer collection abstraction
- `MA0026` - Fix TODO comments
- `MA0051` - Method too long (>60 lines)

## Test attribute policy: HumansFact / HumansTheory

Test projects ban xUnit's bare `[Fact]` and `[Theory]` via
`Microsoft.CodeAnalysis.BannedApiAnalyzers` and `tests/BannedSymbols.txt`
(rule `RS0030`). All test methods must use `[HumansFact]` / `[HumansTheory]`
from `Humans.Testing` (linked into every test project via
`tests/Directory.Build.props`).

Why:
- Default `Timeout = 5000` (5s) caps every test, sync or async, at
  xUnit v3's cooperative-cancellation level. Override per test with
  `[HumansFact(Timeout = N)]` where `N > 0` — the setter rejects
  `Timeout = 0` (infinite) and negative values with `ArgumentException` at
  attribute construction, so a hung test cannot be created by accident.
- Process-level `--blame-hang-timeout 2m` in `.github/workflows/build.yml`
  catches non-cooperative hangs that ignore the cancellation token.

`RS0030` suppressions in test code are forbidden — a CI step in
`build.yml` (`Forbid RS0030 suppressions in test code`) greps `tests/`
(excluding `tests/Humans.Testing/`) and fails the build if any are found.
The HumansFact/HumansTheory declarations themselves use a file-scoped
`#pragma warning disable RS0030` because they declare the project-approved
replacement; that is the only legitimate site.

To run a test that legitimately needs longer than 5s, set
`[HumansFact(Timeout = N)]` with a higher `N`. Existing higher caps in the
codebase are documented per-test (typical: `Timeout = 10000` for
DB-context-setup-heavy tests, `Timeout = 30000` for tests with explicit
delay/retry behaviour).

## ReSharper InspectCode (CLI)

Full analysis using existing `.DotSettings`:

```bash
# PowerShell
./scripts/run-inspectcode.ps1
./scripts/run-inspectcode.ps1 -Severity SUGGESTION -Output Html

# Bash
./scripts/run-inspectcode.sh
./scripts/run-inspectcode.sh SUGGESTION Json
```

**Severity:** HINT, SUGGESTION, WARNING, ERROR
**Output:** Text (default), Xml, Json, Html

Results: `inspectcode-results.*` (gitignored)
First run auto-installs `JetBrains.ReSharper.GlobalTools`.
