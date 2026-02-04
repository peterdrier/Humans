# NuGet Package Update Check

**Purpose:** Check for updates to NuGet packages and analyze security/bug fixes.

**Last Run:** <!-- Update after each check -->
**Frequency:** Monthly (more frequent for security patches)

---

## Quick Start

### Fast Check
```bash
# Check all packages for updates
dotnet list package --outdated

# Check for vulnerable packages
dotnet list package --vulnerable
```

### Detailed Analysis (Use Claude)
```bash
claude "Check NuGet package updates using NUGET_UPDATE_CHECK.md process"
```

---

## Current Package Inventory

<!-- Document your packages here -->

### Framework Packages

| Package | Purpose | Current | Criticality |
|---------|---------|---------|-------------|
| **Microsoft.AspNetCore.*** | ASP.NET Core framework | x.x.x | CRITICAL |
| **Microsoft.Extensions.*** | DI, Configuration, Logging | x.x.x | HIGH |

### Third-Party Packages

| Package | Purpose | Current | Criticality |
|---------|---------|---------|-------------|
<!-- Add your packages here -->

### Development/Testing

| Package | Purpose | Current | Criticality |
|---------|---------|---------|-------------|
| **Meziantou.Analyzer** | Code analyzer | x.x | LOW |
| **xunit** | Test framework | x.x | LOW |

---

## Update Process

### 1. Check for Updates

```bash
dotnet list package --outdated
```

### 2. Categorize Updates

**CRITICAL (Update Immediately):**
- Security vulnerabilities (CVE fixes)
- .NET runtime patches
- Authentication/Identity fixes

**HIGH (Update This Week):**
- Bug fixes affecting functionality
- Performance improvements
- Memory leak fixes

**MEDIUM (Update This Month):**
- Minor bug fixes
- New features we might use

**LOW (Update When Convenient):**
- Development/testing packages
- Non-critical optimizations

### 3. Review Release Notes

**What to Look For:**
- Security fixes (CVE-xxxx) - Immediate action required
- Breaking changes - May require code changes
- Bug fixes - Especially if we've encountered the bug
- New features - Nice to have

### 4. Test Upgrade

```bash
# Create branch
git checkout -b upgrade/nuget-{date}

# Upgrade specific package
dotnet add package {PackageName} --version {Version}

# Build and test
dotnet build
dotnet test

# Commit if successful
git add -A
git commit -m "upgrade: NuGet packages {date}"
```

---

## Decision Matrix

| Factor | Action | Priority |
|--------|--------|----------|
| **Security vulnerability (CVE)** | Upgrade immediately | CRITICAL |
| **.NET patch release** | Upgrade within 1 week | HIGH |
| **Major .NET version** | Plan carefully, test thoroughly | MEDIUM |
| **Third-party security fix** | Upgrade within 1 week | HIGH |
| **Third-party bug fix** | Upgrade within 1 month | LOW |
| **Analyzer/test package** | Upgrade quarterly | LOW |

---

## Testing Checklist

After upgrading packages:

### Build & Unit Tests
- [ ] `dotnet clean`
- [ ] `dotnet restore`
- [ ] `dotnet build` - No errors
- [ ] `dotnet build` - Check for new warnings
- [ ] `dotnet test` - All tests pass

### Application Testing
- [ ] Critical paths work correctly
- [ ] No performance regressions
- [ ] No memory leaks

---

## Rollback Procedure

If upgrade causes issues:

```bash
# Revert specific file
git checkout main -- *.csproj
dotnet restore
dotnet build
```

---

## Security Vulnerability Handling

### When a CVE is Announced

1. **Check if affected:**
   ```bash
   dotnet list package --vulnerable
   ```

2. **Immediate action for CRITICAL/HIGH:**
   ```bash
   git checkout -b security/cve-{number}
   dotnet add package {PackageName} --version {SafeVersion}
   dotnet build && dotnet test
   git commit -m "security: fix CVE-{number}"
   ```

---

## Historical Update Log

<!-- Add entries after each update check -->

**Template:**
```
### YYYY-MM-DD - [Security/Routine] Update
- **Packages Updated:**
  - {Package}: {Old} -> {New} (Reason)
- **Breaking Changes:** None/List
- **Testing:** All tests pass
```

---

## Resources

- [.NET Release Notes](https://github.com/dotnet/core/tree/main/release-notes)
- [NuGet Vulnerability Database](https://github.com/advisories?query=ecosystem%3Anuget)
- [.NET Security Advisories](https://github.com/dotnet/announcements/issues?q=is%3Aopen+is%3Aissue+label%3ASecurity)
