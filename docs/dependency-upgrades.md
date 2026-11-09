# Weekly dependency upgrades

The Sunday routine follows this doc to land one PR with every safe upgrade, so Monday's Dependabot run finds little or nothing. Dependabot stays on as the backstop (`.github/dependabot.yml`). Anyone can also run it by hand: "follow docs/dependency-upgrades.md".

## Scope

- NuGet: `Directory.Packages.props` (central package management) and `.config/dotnet-tools.json`.
- npm: `tests/e2e/package.json`.
- GitHub Actions: `uses:` refs in `.github/workflows/*.yml`.

Out of scope: the .NET SDK band in `global.json` and moving the framework (`Microsoft.*`, EF Core, Npgsql) to a new major. Flag those under **Needs Peter** instead.

## Steps

1. **Preflight.** Branch `deps/weekly-YYYY-MM-DD` off `origin/main`. If last week's upgrade PR is still open, finish this run first, then close the old one as superseded with a link.
2. **Inventory.** `dotnet list Humans.slnx package --outdated` (NuGet), `npm outdated` in `tests/e2e`, and the latest release tag of each action. Pre-releases only where the current pin is already a pre-release.
3. **Review each candidate before applying it.** Read the release notes or changelog between the current and new version. Hold it back (with the reason) when:
   - the version was published less than 3 days ago (lets a compromised or broken release get yanked first);
   - the notes show a breaking change you can't fully fix this run;
   - ownership, license, or maintainers changed, or the package is deprecated;
   - it's a major bump that needs a design call.
4. **Move coupled packages together.** Hangfire.Core + Hangfire.AspNetCore (exact pin); AngleSharp + HtmlSanitizer; the `Microsoft.AspNetCore.*` / `Microsoft.EntityFrameworkCore.*` / `Microsoft.Extensions.*` 10.0.x band as one; Npgsql EF providers with each other. Comments in `Directory.Packages.props` name any other pins.
5. **Apply and fix.** One commit per package or coupled group, then a separate commit for any code it needed. Fix the code properly: new analyzer warnings get fixed, never suppressed ([`no-analyzer-suppressions`](../memory/process/no-analyzer-suppressions.md)); API changes get ported (see the CsvHelper 34 port, `563a6a6ba`). If an EF Core or Npgsql bump changes a model snapshot, stop on that package and hold it back; never hand-edit migrations.
6. **Verify.** `dotnet build Humans.slnx -v quiet` and `dotnet test Humans.slnx -v quiet`, plus `dotnet format whitespace Humans.slnx --verify-no-changes`. For npm, `npx tsc --noEmit` in `tests/e2e`. A package that can't go green is reverted and held back, not forced.
7. **Open one PR** to `origin/main`, titled `chore(deps): weekly upgrades YYYY-MM-DD`, with:
   - **Upgraded:** table of package, from, to, and what changed or was fixed (one line each).
   - **Held back:** package, available version, reason. File an issue on peterdrier/Humans only for one that needs real work.
   - **Needs Peter:** numbered one-line decisions, if any.
8. **Tend it** until CI is green, under the normal steward rules. If nothing is outdated, open no PR and say so in one line.

## Dependabot PRs

Leave them alone: never push to or `update-branch` a Dependabot branch ([`never-update-branch-dependabot-prs`](../memory/process/never-update-branch-dependabot-prs.md)). When this PR covers a version an open Dependabot PR proposes, Dependabot closes its own PR once main moves past it.
