# NuGet Package Update Assessment Process

This document describes the process for evaluating NuGet package updates before applying them.

## Quick Command

```bash
# Get all outdated packages in JSON format
dotnet list package --outdated --format json > outdated-packages.json

# Or view in table format
dotnet list package --outdated
```

## Risk Classification

### By Version Bump Type (SemVer)

| Bump Type | Risk Level | Description |
|-----------|------------|-------------|
| **Major** (X.0.0) | HIGH | Breaking changes expected. Review changelog carefully. |
| **Minor** (0.X.0) | MEDIUM | New features, possible deprecations. Usually safe. |
| **Patch** (0.0.X) | LOW | Bug fixes, security patches. Generally safe to apply. |

### By Package Category

| Category | Risk | Notes |
|----------|------|-------|
| **Microsoft.AspNetCore.*** | MEDIUM | Framework patches. Usually safe but test critical paths. |
| **Microsoft.Identity.*** | MEDIUM | Auth library - test login/logout flows after update. |
| **Database clients** | MEDIUM | Test queries and performance after update. |
| **Serilog.*** | MEDIUM | Logging - major versions may change config format. |
| **Analyzers** | LOW | Dev-only tools - don't affect runtime. |
| **UI Frameworks** | HIGH | Visual/behavioral changes. Test thoroughly. |

## Assessment Checklist

For each package update, check:

### 1. Version Bump Type
- [ ] Is it a major version bump? -> Review breaking changes
- [ ] Is it a minor version bump? -> Check for deprecations
- [ ] Is it a patch? -> Usually safe, check for security fixes

### 2. Changelog Review
- [ ] Find release notes (GitHub releases, NuGet page, docs)
- [ ] Look for "Breaking Changes" section
- [ ] Check for migration guides
- [ ] Note any deprecated APIs you might use

### 3. Dependency Compatibility
- [ ] Check if package requires newer .NET version
- [ ] Verify compatibility with other packages
- [ ] Look for peer dependency requirements

### 4. Application Impact
- [ ] Does it affect critical paths? (auth, data, payments)
- [ ] Does it affect UI components?
- [ ] Are there configuration changes needed?

## Recommended Update Strategy

### Safe to Update Immediately (Patch versions)
- Security patches for Microsoft.AspNetCore.*
- Analyzer updates (Meziantou, Roslynator)
- JetBrains.Annotations

### Update with Basic Testing (Minor versions)
- Azure.Storage.* packages
- Google.Apis.*
- Serilog sinks (non-core)

### Update with Thorough Testing (Major versions)
- Serilog core packages
- Microsoft.Identity.Web
- Database clients

### Schedule Dedicated Time (Major UI framework)
- Any major UI framework version
- Any major ASP.NET Core version

## Automation

Consider enabling Dependabot for automated pull requests:
- `.github/dependabot.yml` configuration
- Automatic PR creation for updates
- Grouped updates by category

## Report Template

When assessing updates, document findings:

```markdown
## Package Update Assessment - [Date]

### High Risk Updates
| Package | Current | Latest | Breaking Changes | Action |
|---------|---------|--------|------------------|--------|
| ... | ... | ... | ... | ... |

### Medium Risk Updates
| Package | Current | Latest | Notes | Action |
|---------|---------|--------|-------|--------|
| ... | ... | ... | ... | ... |

### Low Risk Updates (Safe to Apply)
- Package A: x.x.x -> x.x.y (bug fixes)
- Package B: x.x.x -> x.x.y (security patch)

### Recommendations
1. ...
2. ...
```
