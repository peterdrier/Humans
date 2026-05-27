# Code Analysis

## Roslyn Analyzers (Build-time)

Runs during `dotnet build`:
- **Meziantou.Analyzer** (MA0xxx) - Code quality
- **Roslynator.Analyzers** (RCS0xxx) - C# analysis
- **Microsoft.VisualStudio.Threading.Analyzers** - Async/threading
- **Humans.Analyzers** (HUMxxxx) - In-repo architecture rules ([catalogue + pattern](#humansanalyzers-build-time-architecture-rules))

<!-- freshness:auto id="suppressions" prompt-file=".claude/skills/freshness-sweep/prompts/suppressions.md" -->

**Common suppressions in `Directory.Build.props`:**
- `CS1591` - Missing XML comment for publicly visible type or member
- `MA0048` - File name must match type name
- `MA0016` - Prefer using collection abstraction instead of implementation
- `MA0026` - Fix TODO comment
- `MA0051` - Method is too long
- `VSTHRD200` - Use "Async" suffix for async methods (not required for ASP.NET Core controller actions)
- `HUM_PROFILE_ISSUSPENDED` - Legacy reads of `Profile.IsSuspended` permitted until lazy-State-backfill follow-up (issue #635 §15i)
- `HUM_USER_NORMALIZEDEMAIL` - Legacy reads of Identity's shadow-populated `User.NormalizedEmail` permitted (issue #635 §15i)
- `xUnit1051` - Pass `TestContext.Current.CancellationToken` to methods accepting `CancellationToken` (test projects only, via `tests/Directory.Build.props`)
- `HUM_USER_DISPLAYNAME` - Legacy `User.DisplayName` seeded in test fixtures (account creation/merge/deletion, cached `UserInfo` fallback) — not render-path violations (test projects only, via `tests/Directory.Build.props`)

<!-- /freshness:auto -->

## Test attribute policy: HumansFact / HumansTheory

Test projects ban xUnit's bare `[Fact]` and `[Theory]` via
`Microsoft.CodeAnalysis.BannedApiAnalyzers` and `tests/BannedSymbols.txt`
(rule `RS0030`). All test methods must use `[HumansFact]` / `[HumansTheory]`
from `Humans.Testing` (linked into every test project via
`tests/Directory.Build.props`).

Why:
- Default `Timeout = 30000` (30s) caps every test, sync or async, at
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

## Humans.Analyzers (Build-time Architecture Rules)

In-repo Roslyn analyzer project at `src/Humans.Analyzers/`. Replaces fragile
Mono.Cecil IL-scan tests for call-site architecture rules ("X may not call Y",
"only Z may write W"). See [nobodies-collective/Humans#695](https://github.com/nobodies-collective/Humans/issues/695) for the migration story.

### Catalogue

Rule    | Title                                                                                          | Severity
--------|-----------------------------------------------------------------------------------------------|---------
HUM0001 | Reference to deleted email-identity-decoupling legacy member                                  | Error
HUM0002 | Identity column on User must not be written from Application or Web                           | Error
HUM0003 | UserManager.FindByEmailAsync / FindByNameAsync must not be called from Application or Web    | Error
HUM0004 | Profile.IsSuspended must not be written outside the allowlisted dual-writers                  | Error
HUM0005 | IUserEmailService.UpdateEmailAsync may only be called from AccountController                  | Error
HUM0006 | IUserEmailRepository.UpdateEmailAsync may only be called from UserEmailService                | Error
HUM0007 | Concurrency tokens are forbidden in live source                                               | Error
HUM0008 | Controllers may not inject HumansDbContext                                                    | Error
HUM0009 | Class uses HumansDbContext but does not implement IRepository                                 | Error
HUM0015 | Type decorated with [SurfaceBudget(N)] declares more than N public-instance methods           | Error
HUM0016 | Type decorated with [SurfaceBudget(N)] declares fewer than N public-instance methods (slack) | Error
HUM0020 | Caching decorator references a repository directly instead of the keyed inner service         | Error
HUM0021 | Cross-domain navigation property must not be read                                            | Warning
HUM0024 | EF configuration creates a navigation join across section boundaries                         | Error
HUM0025 | A DbSet table is referenced (read or written) by more than one repository                    | Error

Authoritative declaration: `src/Humans.Analyzers/AnalyzerReleases.Unshipped.md`
(plus `AnalyzerReleases.Shipped.md` once we cut a 1.0).

### How it ships

`src/Directory.Build.props` adds a `ProjectReference` with
`OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"` to every
project under `src/` except the analyzer itself. The analyzer compiles to
`netstandard2.0` and binds to `Microsoft.CodeAnalysis.CSharp` 5.0.0 (the
version EF Core Design pulls in on .NET 10). Tests do *not* attach the
analyzer to their own compilation — they instantiate analyzers directly via
`AnalyzerTestHarness` and feed them synthetic compilations.

### When to write an analyzer vs. a test

| Pattern shape | Tool | Why |
|---|---|---|
| "X may not call Y" / "only X may call Y" / "must not write to property" | **Analyzer** | Fires in-editor + at build with a precise source location. The compiler's semantic model handles inheritance, generics, async lowering. |
| "no new violations from here, baseline absorbs the existing N" | **Ratchet test** (`Architecture/Rules/`, `Architecture/Ratchet/`) | Analyzers don't natively express accumulated-debt baselines. Build one when there's a third rule that needs it, not for the first. |
| "this symbol must exist" / "this interface must implement that marker" | **Reflection test** | Analyzers fire on present code, not absent code. |
| "in this folder, no calls of shape X" / "section ownership comes from directory layout" | **Filesystem-aware ratchet test** | Analyzers see compilations, not folders. |
| EF migration-file checks (no `Drop*` in `Up()`) | **Ratchet test** | Migration files are EF-generated; an analyzer would fire on legitimate ops. |

The 10 ratchet rules under `tests/Humans.Application.Tests/Architecture/Rules/`
and the boundary scans in `ServiceBoundaryArchitectureTests.cs` all fall into
"ratchet" / "marker" / "filesystem-aware" buckets — they stay as tests.

### Writing a new analyzer

Cost ≈ 50 lines for the analyzer + 30 for the tests once the project exists.

1. Add the rule file `src/Humans.Analyzers/<Name>Analyzer.cs` deriving from
   `DiagnosticAnalyzer`. Use the next free `HUM00xx` id.
2. Pick a scope — `AssemblyScope.IsApplicationOrWeb` is the common one. Gate
   in `OnCompilationStart` before registering operation actions.
3. Register on the smallest set of operation kinds that fits the rule
   (`OperationKind.Invocation`, `OperationKind.PropertyReference`,
   `OperationKind.SimpleAssignment`). Match by symbol metadata names from
   `Internal/SymbolExtensions.cs` — avoid string-comparing `Operation.Syntax`.
4. Add the rule entry to `AnalyzerReleases.Unshipped.md`.
5. Add a test file under `tests/Humans.Analyzers.Tests/` with at least one
   positive and one negative case. Use `AnalyzerTestHarness.RunAsync` with the
   target assembly name to exercise the scope guard.
6. Run `dotnet build Humans.slnx -v quiet` — the new rule fires solution-wide
   from the next compile.

### Worked example: "only X may call Y"

`EmailMutationPathsAnalyzer.cs` is the smallest one with a caller allowlist:

- Pin both the *callee* (full interface + method name) and the *allowed caller*
  (top-level containing type via `ISymbol.ContainingTopLevelType()`).
- Forbid in scope assemblies (`AssemblyScope.IsApplicationWebOrInfrastructure`);
  every call site inside scope is checked. The two allowlisted callers
  themselves live in scope assemblies, the type-name guard admits them.
- One diagnostic per forbidden site, with the rule's `memory/architecture/*.md`
  atom named in the message so a confused reader has somewhere to start.

### History / context

The first analyzer (`UserEmailLegacyFieldAnalyzer`) and the project scaffold
shipped together in the migration of four IL-scan tests + the email-mutation
pin. Pre-migration these rules ran via Mono.Cecil IL walks in
`tests/Humans.Application.Tests/Architecture/`. The fragility — silent misses
on expression trees + reflection + dynamic dispatch, codegen-evolution risk on
`async`/`ValueTask` lowering, test-time-only feedback — is documented on
[issue 695](https://github.com/nobodies-collective/Humans/issues/695).

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
