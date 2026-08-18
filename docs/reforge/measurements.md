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
