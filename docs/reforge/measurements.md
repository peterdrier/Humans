# Reforge measurements

Append-only. One section per run, **newest last**. How to take a reading and how to
name the files: [`README.md`](README.md).

---

## 2026-08-16 — Humans d237f3cc0 — reforge 0.28.1

First reading. Full archive (`--all --top-symbols 0`), no truncation — JSON is 1.8 MB.
Build was clean (0 errors, 26 warnings), so the score is authoritative;
`--allow-degraded` was not used.

Raw: [`runs/2026-08-16-d237f3cc0-reforge0.28.1.json`](runs/2026-08-16-d237f3cc0-reforge0.28.1.json)
· [`runs/2026-08-16-d237f3cc0-reforge0.28.1.md`](runs/2026-08-16-d237f3cc0-reforge0.28.1.md)

reforge version string: `0.28.1+eda7cd07fe03134a6de02b0a3702704b8921b739`

### (a) Headline

| Metric | Value |
|--------|-------|
| total | 13,923 |
| surfaceTotal | 10,770 (77.4%) |
| internalComplexityTotal | 3,153 (22.6%) |
| typesAnalyzed | 2,842 |
| sections (groups) | 47 |
| degraded | false |

```json
{
  "total": 13923,
  "surfaceTotal": 10770,
  "internalComplexityTotal": 3153,
  "typesAnalyzed": 2842,
  "sections": 47,
  "degraded": false,
  "configPath": "H:\\source\\Humans\\.worktrees\\reforge-archive\\reforge.surface-score.json"
}
```

### (b) The six read-surface rules reforge#19 proposes retiring

Claim under test: **1,230 pts, 7%**. Measured subtotal is **1,307**, which is
**9.4% of total** / **12.1% of surfaceTotal** — so the claim understates both the
points and the share.

Two caveats on the arithmetic. `crossSectionWriteSurface` does not appear in `byRule`
at all in this run (0 pts, not merely small). And `canonicalReadDtoReturn` is a
**credit**, not a cost: −87. Retiring the six as a set therefore removes 1,394 pts of
charge and 87 pts of credit; the net 1,307 is what the subtotal reports.

| Rule | Points |
|------|-------:|
| crossSectionReadInterface | 528 |
| readServiceInterfaceMethod | 498 |
| readSurfaceProjectionMethod | 272 |
| writeCapableInterfaceUsedReadOnly | 96 |
| crossSectionWriteSurface | *(absent)* |
| canonicalReadDtoReturn | −87 |
| **subtotal** | **1,307** |

```json
{
  "rules": {
    "crossSectionReadInterface": 528,
    "readServiceInterfaceMethod": 498,
    "readSurfaceProjectionMethod": 272,
    "writeCapableInterfaceUsedReadOnly": 96,
    "canonicalReadDtoReturn": -87
  },
  "subtotal": 1307
}
```

### (c) The internal axis per rule

Claim under test: **88% extract-method-satisfiable**. The three size rules
(`longMethod` + `cognitiveComplexity` + `largeClass`) sum to 2,767 of 3,153 =
**87.8%** — the claim holds.

The seven rules queried sum to exactly `internalComplexityTotal` (3,153), so the
internal axis is entirely these rules and nothing else.
`genericActionDispatcher` is absent from `byRule` in this run.

| Rule | Points | Share of internal |
|------|-------:|------------------:|
| longMethod | 1,490 | 47.3% |
| cognitiveComplexity | 832 | 26.4% |
| largeClass | 445 | 14.1% |
| mutationModeParameter | 330 | 10.5% |
| actionDispatcher | 40 | 1.3% |
| flagsControlFlow | 16 | 0.5% |
| genericActionDispatcher | *(absent)* | — |
| **total** | **3,153** | **100%** |

```json
{
  "longMethod": 1490,
  "cognitiveComplexity": 832,
  "largeClass": 445,
  "mutationModeParameter": 330,
  "actionDispatcher": 40,
  "flagsControlFlow": 16
}
```

### (d) Size-rule distributions — what "re-base the unit" needs

Each rule's `sum` here matches its `byRule` figure exactly, confirming these
distributions cover every entry (only true because the run used `--all`).

All three are long-tailed: the median entry is worth 2–10 pts while the max is 62–75.
`longMethod` in particular spreads 1,490 pts across 303 entries at a p50 of 2, so
re-basing its unit moves far more points than chasing its worst offenders.
`largeClass` is the opposite shape — 445 pts across only 16 entries, p90 of 65.

| Rule | count | sum | max | p50 | p90 |
|------|------:|----:|----:|----:|----:|
| cognitiveComplexity | 89 | 832 | 72 | 5 | 23 |
| longMethod | 303 | 1,490 | 62 | 2 | 17 |
| largeClass | 16 | 445 | 75 | 10 | 65 |

```
== cognitiveComplexity
{
  "count": 89,
  "sum": 832,
  "max": 72,
  "p50": 5,
  "p90": 23
}
== longMethod
{
  "count": 303,
  "sum": 1490,
  "max": 62,
  "p50": 2,
  "p90": 17
}
== largeClass
{
  "count": 16,
  "sum": 445,
  "max": 75,
  "p50": 10,
  "p90": 65
}
```

### (e) Top 20 symbols across rules

`.topSymbols`, not `.symbols` — the key in the JSON is `topSymbols`, and
`--top-symbols 0` means *uncapped* (2,570 symbols in this run), not zero.

<details>
<summary>Top 20 cross-rule symbols</summary>

| # | Symbol | File | Total |
|--:|--------|------|------:|
| 1 | ProfileController | `src/Sections/Humans.Users/Controllers/ProfileController.cs:48` | 139 |
| 2 | SeedAsync | `src/Sections/Humans.Development/Services/DevelopmentDashboardSeeder.cs:80` | 134 |
| 3 | TeamService | `src/Sections/Humans.Teams/Services/TeamService.cs:29` | 93 |
| 4 | GoogleWorkspaceSyncService | `src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceSyncService.cs:28` | 91 |
| 5 | ShiftManagementService | `src/Sections/Humans.Shifts/Services/ShiftManagementService.cs:32` | 81 |
| 6 | RunTurnAsync | `src/Sections/Humans.Agent/Services/AgentService.cs:260` | 79 |
| 7 | GetMemberDashboardAsync | `src/Humans.Application/Services/Dashboard/DashboardService.cs:33` | 76 |
| 8 | UsersAdminController | `src/Sections/Humans.Users/Controllers/UsersAdminController.cs:31` | 76 |
| 9 | DevPersonaSeeder | `src/Sections/Humans.Development/Services/DevPersonaSeeder.cs:38` | 66 |
| 10 | UserProfileSaveCommand | `src/Sections/Humans.Users.Contracts/UserProfileCommands.cs:26` | 60 |
| 11 | LogAsync | `src/Sections/Humans.AuditLog.Contracts/IAuditLogService.cs:22` | 56 |
| 12 | Configure | `src/Sections/Humans.Teams/Data/Configurations/TeamConfiguration.cs:23` | 55 |
| 13 | UserRepository | `src/Sections/Humans.Users/Data/Repositories/UserRepository.ContactFields.cs:10` | 55 |
| 14 | ReconcileOAuthIdentityAsync | `src/Sections/Humans.Users/Services/UserEmailService.cs:934` | 53 |
| 15 | AccountDeletionService | `src/Humans.Application/Services/Users/AccountLifecycle/AccountDeletionService.cs:19` | 50 |
| 16 | StreamAsync | `src/Sections/Humans.Agent/Services/Anthropic/AnthropicClient.cs:33` | 48 |
| 17 | UserProfileOnboardingCommand | `src/Sections/Humans.Users.Contracts/UserProfileCommands.cs:9` | 46 |
| 18 | RunAsync | `src/Sections/Humans.Users/Services/UserEmailProviderBackfillService.cs:21` | 45 |
| 19 | ExpenseReportService | `src/Sections/Humans.Expenses/Services/ExpenseReportService.cs:31` | 44 |
| 20 | GateService | `src/Sections/Humans.Gate/Services/GateService.cs:30` | 44 |

```json
[
  {
    "symbol": "ProfileController",
    "file": "src/Sections/Humans.Users/Controllers/ProfileController.cs",
    "line": 48,
    "total": 139,
    "byRule": {
      "largeClass": 65,
      "crossSectionFullService": 64,
      "crossSectionReadInterface": 10
    }
  },
  {
    "symbol": "SeedAsync",
    "file": "src/Sections/Humans.Development/Services/DevelopmentDashboardSeeder.cs",
    "line": 80,
    "total": 134,
    "byRule": {
      "cognitiveComplexity": 72,
      "longMethod": 62
    }
  },
  {
    "symbol": "TeamService",
    "file": "src/Sections/Humans.Teams/Services/TeamService.cs",
    "line": 29,
    "total": 93,
    "byRule": {
      "largeClass": 75,
      "crossSectionFullService": 16,
      "crossSectionReadInterface": 2
    }
  },
  {
    "symbol": "GoogleWorkspaceSyncService",
    "file": "src/Sections/Humans.GoogleIntegration/Services/GoogleWorkspaceSyncService.cs",
    "line": 28,
    "total": 91,
    "byRule": {
      "largeClass": 65,
      "crossSectionFullService": 24,
      "crossSectionReadInterface": 2
    }
  },
  {
    "symbol": "ShiftManagementService",
    "file": "src/Sections/Humans.Shifts/Services/ShiftManagementService.cs",
    "line": 32,
    "total": 81,
    "byRule": {
      "largeClass": 65,
      "crossSectionFullService": 16
    }
  },
  {
    "symbol": "RunTurnAsync",
    "file": "src/Sections/Humans.Agent/Services/AgentService.cs",
    "line": 260,
    "total": 79,
    "byRule": {
      "longMethod": 52,
      "cognitiveComplexity": 27
    }
  },
  {
    "symbol": "GetMemberDashboardAsync",
    "file": "src/Humans.Application/Services/Dashboard/DashboardService.cs",
    "line": 33,
    "total": 76,
    "byRule": {
      "longMethod": 49,
      "cognitiveComplexity": 16,
      "dashboardAdminPageName": 6,
      "applicationServiceMethod": 5
    }
  },
  {
    "symbol": "UsersAdminController",
    "file": "src/Sections/Humans.Users/Controllers/UsersAdminController.cs",
    "line": 31,
    "total": 76,
    "byRule": {
      "crossSectionFullService": 48,
      "writeCapableInterfaceUsedReadOnly": 24,
      "crossSectionReadInterface": 4
    }
  },
  {
    "symbol": "DevPersonaSeeder",
    "file": "src/Sections/Humans.Development/Services/DevPersonaSeeder.cs",
    "line": 38,
    "total": 66,
    "byRule": {
      "crossSectionFullService": 64,
      "crossSectionReadInterface": 2
    }
  },
  {
    "symbol": "UserProfileSaveCommand",
    "file": "src/Sections/Humans.Users.Contracts/UserProfileCommands.cs",
    "line": 26,
    "total": 60,
    "byRule": {
      "parameterBagInput": 60
    }
  },
  {
    "symbol": "LogAsync",
    "file": "src/Sections/Humans.AuditLog.Contracts/IAuditLogService.cs",
    "line": 22,
    "total": 56,
    "byRule": {
      "mutationModeParameter": 30,
      "fullServiceInterfaceMethod": 16,
      "methodParameterOverflow": 10
    }
  },
  {
    "symbol": "Configure",
    "file": "src/Sections/Humans.Teams/Data/Configurations/TeamConfiguration.cs",
    "line": 23,
    "total": 55,
    "byRule": {
      "longMethod": 55
    }
  },
  {
    "symbol": "UserRepository",
    "file": "src/Sections/Humans.Users/Data/Repositories/UserRepository.ContactFields.cs",
    "line": 10,
    "total": 55,
    "byRule": {
      "largeClass": 55
    }
  },
  {
    "symbol": "ReconcileOAuthIdentityAsync",
    "file": "src/Sections/Humans.Users/Services/UserEmailService.cs",
    "line": 934,
    "total": 53,
    "byRule": {
      "longMethod": 53
    }
  },
  {
    "symbol": "AccountDeletionService",
    "file": "src/Humans.Application/Services/Users/AccountLifecycle/AccountDeletionService.cs",
    "line": 19,
    "total": 50,
    "byRule": {
      "crossSectionFullService": 48,
      "crossSectionReadInterface": 2
    }
  },
  {
    "symbol": "StreamAsync",
    "file": "src/Sections/Humans.Agent/Services/Anthropic/AnthropicClient.cs",
    "line": 33,
    "total": 48,
    "byRule": {
      "cognitiveComplexity": 27,
      "longMethod": 21
    }
  },
  {
    "symbol": "UserProfileOnboardingCommand",
    "file": "src/Sections/Humans.Users.Contracts/UserProfileCommands.cs",
    "line": 9,
    "total": 46,
    "byRule": {
      "parameterBagInput": 26,
      "inlineParameterObjectConstruction": 20
    }
  },
  {
    "symbol": "RunAsync",
    "file": "src/Sections/Humans.Users/Services/UserEmailProviderBackfillService.cs",
    "line": 21,
    "total": 45,
    "byRule": {
      "cognitiveComplexity": 40,
      "longMethod": 5
    }
  },
  {
    "symbol": "ExpenseReportService",
    "file": "src/Sections/Humans.Expenses/Services/ExpenseReportService.cs",
    "line": 31,
    "total": 44,
    "byRule": {
      "crossSectionFullService": 32,
      "largeClass": 10,
      "crossSectionReadInterface": 2
    }
  },
  {
    "symbol": "GateService",
    "file": "src/Sections/Humans.Gate/Services/GateService.cs",
    "line": 30,
    "total": 44,
    "byRule": {
      "crossSectionFullService": 40,
      "crossSectionReadInterface": 4
    }
  }
]
```

</details>

---

## 2026-10-21 — Humans 83c00df02 + Users decomposition commit 16 — reforge 0.29.0

Taken on the end of the Users decomposition (commits 0 to 15 plus the commit 16 cleanup, tree
uncommitted at the time, so the stamp carries the parent SHA `83c00df02` and `c16`). Full archive
(`--all --top-symbols 0`), no truncation — JSON is 2.5 MB. Build clean (0 errors), `degraded: false`;
`--allow-degraded` was not used.

Raw: [`runs/2026-10-21-83c00df02-c16-reforge0.29.0.json`](runs/2026-10-21-83c00df02-c16-reforge0.29.0.json)
· [`runs/2026-10-21-83c00df02-c16-reforge0.29.0.md`](runs/2026-10-21-83c00df02-c16-reforge0.29.0.md)

reforge version string: `0.29.0+7bc639e8b15a66c3bcf253e06819ee857f38cb02`

**Not comparable with the 2026-08-16 reading.** reforge 0.29.0 scores differently (it splits
`Contracts` surface from main surface and counts `I*Write` contracts), so the same code now reads
higher: the pre-decomposition parent `d34c5a10`, re-measured with 0.29.0, totals 17,620 against
13,923 under 0.28.1. The like-for-like comparison below is `d34c5a10` against this tree, both
0.29.0 (the parent was measured in a throwaway worktree, not archived).

| Metric | 2026-08-16 (0.28.1) | d34c5a10 (0.29.0) | This tree (0.29.0) |
|--------|---------------------|-------------------|--------------------|
| total | 13,923 | 17,620 | 18,436 |
| surfaceTotal | 10,770 | 13,431 | 14,297 |
| internalComplexityTotal | 3,153 | 4,189 | 4,139 |
| typesAnalyzed | 2,842 | 3,764 | 3,883 |

### Section movement (0.29.0, like-for-like)

| Section | Before (d34c5a10) | After (this tree) | Change |
|---|---|---|---|
| Users | 3,625 | 2,975 | -650 |
| Email | 516 | 839 | +323 |
| Finance | 967 | 1,149 | +182 |
| GoogleIntegration | 922 | 1,085 | +163 |
| Tickets | 744 | 870 | +126 |
| Consent | 309 | 401 | +92 |
| Governance | 532 | 550 | +18 |
| Cantina | 67 | 134 | +67 |
| Auth | 300 | 350 | +50 |
| Gdpr | 124 | 155 | +31 |
| Campaigns | 116 | 136 | +20 |
| Debug | 20 | 39 | +19 |
| Shifts | 1,518 | 1,527 | +9 |
| **Solution total** | 17,620 | 18,436 | +816 |

Users lost 650 points (3,625 to 2,975, minus 18%) and 3,750 production lines (19,482 to 15,732). Its
remaining 2,975 is 509 main surface, 2,193 `Users.Contracts` surface and 273 internal complexity: what is
left is mostly the contracts leaf (`IUserService`, `IUserWrite`, `UserInfo`, the merge and contributor
seams), not screens or services. The plan's estimate of about 1,700 for Users was made on the 0.28.1
scale and does not hold on the 0.29.0 one; the receiving sections absorbed more than the plan's 100 to
400 each (Email +323, Finance +182, GoogleIntegration +163, Tickets +126), consistent with each
gaining a contracts surface. The solution total rose 816 (4.6%): the moved code is still counted, now with a
repository, an `I<Section>Write`, a user-merge fold and an export contributor per target.

---

## 2026-10-29 — Humans b35ab2e39 — reforge 0.29.0

Taken on `origin/main` `b35ab2e39` (no code changes on top; a docs-only stats commit landed on the branch mid-run). Full archive (`--all --top-symbols 0`), no truncation — JSON is 2.4 MB. Build clean (0 errors, 3 `HUM_USER_DISPLAYNAME` warnings), `build.degraded: false`; `--allow-degraded` was not used.

Raw JSON: `2026-10-29-b35ab2e39-reforge0.29.0.json` in the local archive (not committed; see README).
· [`runs/2026-10-29-b35ab2e39-reforge0.29.0.md`](runs/2026-10-29-b35ab2e39-reforge0.29.0.md)

reforge version string: `0.29.0+7bc639e8b15a66c3bcf253e06819ee857f38cb02` (same as the 2026-10-21 entry, so the comparison is like-for-like on tool version).

Note: the 2026-10-21 entry preserved no `jq` invocations, so the queries below follow the 2026-08-16 entry's structure (a to e), re-expressed against the 0.29.0 JSON (`.build.degraded`, not top-level `.degraded`).

### (a) Headline

| Metric | 2026-10-21 | This tree | Delta |
|---|---:|---:|---:|
| total | 18,436 | 17,568 | -868 |
| surfaceTotal | 14,297 | 13,702 | -595 |
| internalComplexityTotal | 4,139 | 3,866 | -273 |
| typesAnalyzed | 3,883 | 3,971 | +88 |
| sections (groups) | 48 | 50 | +2 |
| degraded | false | false | |

Baseline verdict (`--baseline runs/2026-10-21-83c00df02-c16-reforge0.29.0.json`): `verdict=improved improvement=True`, surface 14297 -> 13702 (-595), internalComplexity 4139 -> 3866 (-273), total 18,436 -> 17,568 (-868).

One `traded` verdict: **Base** — surface improved by 63 points but implementation complexity worsened by 1 (cognitiveComplexity +2 in `ProcessAsync`, `Convert`); not counted as an improvement. Other Suspicious Improvements flagged: parameter-bag consolidation for solution (+110: `ProfileSaveRequest`, `CreateShiftInput`, `CreateRotaInput`) and Users (+112: `ProfileSaveRequest`, `UserEmailAddCommand`, `UserEmailReconcilePlanCommand`); a helper-extraction-without-concept-deleted advisory for GoogleIntegration (4 methods moved sideways); and canonical-consolidation advisories for Auth, Campaigns, Email, Finance, Governance, Notifications, Teams, Tickets, Users.

```json
{
  "total": 17568,
  "surfaceTotal": 13702,
  "internalComplexityTotal": 3866,
  "typesAnalyzed": 3971,
  "sections": 50,
  "degraded": false
}
```

```
jq '{total,surfaceTotal,internalComplexityTotal,typesAnalyzed,sections:(.groups|length),degraded:.build.degraded}' docs/reforge/latest.json
```

#### Section movement vs 2026-10-21 (non-zero deltas)

| Section | 2026-10-21 | This tree | Change |
|---|---:|---:|---:|
| Email | 839 | 662 | -177 |
| Users | 2,975 | 2,847 | -128 |
| GoogleIntegration | 1,085 | 986 | -99 |
| Teams | 1,154 | 1,058 | -96 |
| Shifts | 1,527 | 1,440 | -87 |
| Tickets | 870 | 789 | -81 |
| Base | 749 | 687 | -62 |
| Governance | 550 | 506 | -44 |
| Cantina | 134 | 92 | -42 |
| Holded | 479 | 437 | -42 |
| Camps | 477 | 436 | -41 |
| Auth | 350 | 317 | -33 |
| Gate | 104 | 81 | -23 |
| Consent | 401 | 382 | -19 |
| Store | 221 | 207 | -14 |
| Campaigns | 136 | 122 | -14 |
| Workgroups | 175 | 163 | -12 |
| CityPlanning | 224 | 212 | -12 |
| Expenses | 478 | 467 | -11 |
| Finance | 1,149 | 1,141 | -8 |
| EarlyEntry | 58 | 52 | -6 |
| Development | 238 | 232 | -6 |
| Surveys | 486 | 481 | -5 |
| Settings | 85 | 82 | -3 |
| Web | 450 | 447 | -3 |
| Budget | 253 | 252 | -1 |
| Backdoor | 65 | 64 | -1 |
| Calendar | 142 | 144 | +2 |
| Stripe | 68 | 70 | +2 |
| Onboarding | 89 | 92 | +3 |
| LowIncome | 0 | 4 | +4 |
| Feedback | 146 | 151 | +5 |
| MailerLite | 248 | 254 | +6 |
| Gdpr | 155 | 163 | +8 |
| Guide | 37 | 55 | +18 |
| Notifications | 295 | 314 | +19 |
| Agent | 389 | 410 | +21 |
| Preapprovals | 107 | 132 | +25 |
| Grants | 0 | 89 | +89 |
| **Solution total** | 18,436 | 17,568 | -868 |

### (b) Read-surface rules (the six reforge#19 proposes retiring)

Measured subtotal **4,786** = 27.2% of total / 34.9% of surfaceTotal (2026-10-21: 4,842). `crossSectionWriteSurface` is absent from `byRule`; `canonicalReadDtoReturn` is a credit.

| Rule | 2026-10-21 | This tree |
|---|---:|---:|
| crossSectionReadInterface | 1,266 | 1,302 |
| readServiceInterfaceMethod | 2,232 | 2,190 |
| readSurfaceProjectionMethod | 1,296 | 1,264 |
| writeCapableInterfaceUsedReadOnly | 120 | 96 |
| canonicalReadDtoReturn | -72 | -66 |
| crossSectionWriteSurface | (absent) | (absent) |
| **subtotal** | **4,842** | **4,786** |

```json
{
  "rules": {
    "crossSectionReadInterface": 1302,
    "readServiceInterfaceMethod": 2190,
    "readSurfaceProjectionMethod": 1264,
    "writeCapableInterfaceUsedReadOnly": 96,
    "canonicalReadDtoReturn": -66
  },
  "subtotal": 4786
}
```

```
jq '.byRule|{crossSectionReadInterface,readServiceInterfaceMethod,readSurfaceProjectionMethod,writeCapableInterfaceUsedReadOnly,crossSectionWriteSurface,canonicalReadDtoReturn}' docs/reforge/latest.json
```

### (c) The internal axis per rule

The three size rules sum to 3,410 of 3,866 = 88.2%. The six rules queried sum to 3,866 = `internalComplexityTotal` exactly.

| Rule | 2026-10-21 | This tree | Share of internal |
|---|---:|---:|---:|
| longMethod | 1,930 | 1,701 | 44.0% |
| cognitiveComplexity | 1,123 | 1,144 | 29.6% |
| largeClass | 630 | 565 | 14.6% |
| mutationModeParameter | 360 | 360 | 9.3% |
| actionDispatcher | 80 | 80 | 2.1% |
| flagsControlFlow | 16 | 16 | 0.4% |
| genericActionDispatcher | (absent) | (absent) | — |

```json
{
  "longMethod": 1701,
  "cognitiveComplexity": 1144,
  "largeClass": 565,
  "mutationModeParameter": 360,
  "actionDispatcher": 80,
  "flagsControlFlow": 16
}
```

```
jq '.byRule|{longMethod,cognitiveComplexity,largeClass,mutationModeParameter,actionDispatcher,flagsControlFlow,genericActionDispatcher}' docs/reforge/latest.json
```

### (d) Size-rule distributions

Each `sum` matches its `byRule` figure (valid because the run used `--all`).

| Rule | count | sum | max | p50 | p90 |
|---|---:|---:|---:|---:|---:|
| cognitiveComplexity | 126 | 1,144 | 77 | 5 | 20 |
| longMethod | 366 | 1,701 | 68 | 2 | 16 |
| largeClass | 15 | 565 | 95 | 20 | 75 |

```
jq --arg r <rule> '[.groups[].topEntries[]?|select(.rule==$r)|.points]|sort|{count:length,sum:add,max:max,p50:.[(length*0.5|floor)],p90:.[(length*0.9|floor)]}' docs/reforge/latest.json
```

### (e) Top 20 symbols across rules

`.topSymbols` (uncapped: 3165 symbols in this run).

<details>
<summary>Top 20 cross-rule symbols</summary>

| # | Symbol | File | Total |
|--:|--------|------|------:|
| 1 | SeedAsync | `src/Sections/Humans.Development/Services/DevelopmentDashboardSeeder.cs` | 141 |
| 2 | ProfileSaveRequest | `src/Sections/Humans.Users.Contracts/ProfileSaveRequest.cs` | 132 |
| 3 | BookOneTransferAsync | `src/Sections/Humans.Finance/Services/SepaBankBooking.cs` | 131 |
| 4 | SurveyService | `src/Sections/Humans.Surveys/Services/SurveyService.cs` | 113 |
| 5 | WorkgroupService | `src/Sections/Humans.Workgroups/Services/WorkgroupService.cs` | 97 |
| 6 | RunTurnAsync | `src/Sections/Humans.Agent/Services/AgentService.cs` | 85 |
| 7 | CreateShiftInput | `src/Sections/Humans.Shifts.Contracts/IShiftSeeding.cs` | 84 |
| 8 | TeamService | `src/Sections/Humans.Teams/Services/TeamService.cs` | 81 |
| 9 | ExpenseReportService | `src/Sections/Humans.Expenses/Services/ExpenseReportService.cs` | 77 |
| 10 | StreamAsync | `src/Sections/Humans.Agent/Services/Anthropic/AnthropicClient.cs` | 71 |
| 11 | AdvanceWizardAsync | `src/Sections/Humans.Surveys/Services/SurveyService.cs` | 70 |
| 12 | AssemblyVoteService | `src/Sections/Humans.Governance/Services/AssemblyVoteService.cs` | 69 |
| 13 | Service | `src/Sections/Humans.Finance/Services/Service.cs` | 69 |
| 14 | ShiftManagementService | `src/Sections/Humans.Shifts/Services/ShiftManagementService.cs` | 69 |
| 15 | ProcessAsync | `src/Humans.Base/TagHelpers/MarkdownEditorTagHelper.cs` | 64 |
| 16 | CreateRotaInput | `src/Sections/Humans.Shifts.Contracts/IShiftSeeding.cs` | 62 |
| 17 | GetSepaPayoutsAsync | `src/Sections/Humans.Finance/Services/SepaBankBooking.cs` | 62 |
| 18 | LogAsync | `src/Sections/Humans.AuditLog/Contracts/IAuditLogService.cs` | 60 |
| 19 | ReconcileOAuthIdentityAsync | `src/Sections/Humans.Users/Services/UserEmailService.cs` | 57 |
| 20 | Configure | `src/Sections/Humans.Teams/Data/Configurations/TeamConfiguration.cs` | 55 |

```
jq '.topSymbols[:20]' docs/reforge/latest.json
```

</details>
