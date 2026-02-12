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
