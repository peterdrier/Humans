<!-- freshness:triggers
  src/Sections/Humans.Grants/**
  src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs
-->
<!-- freshness:flag-on-change
  Who issues a program's grants, the one-live-grant rule, expiry, or the notice paths may have shifted; verify against the invariants doc.
-->

# Grants

## Business Context

The association hands individual people entitlements: a cheaper ticket, early entry for build, staying late for strike. Campaigns did this with imported codes and email waves; from 2027 a grant records the entitlement itself, and how the thing is delivered is a separate, later concern (nobodies-collective/Humans#2099). Some programs are filled by people (a team lead picks who stays for strike); others follow from another section's decision (Low Income decides who qualifies).

## User Stories

**As Board or Admin, I create a program** (name, what it entitles, event, optional default expiry, issuer) and name its managers.
- The issuer is either its managers by hand, or a source section by source key.
- At most one open program per source key; a second is refused.
- A closed program takes no new grants; its existing grants stay.

**As a program manager, I issue grants by hand** to one or more people, optionally with an expiry that overrides the program's.
- People already holding a live grant in the program are skipped.
- I see and act only on the programs I manage; I cannot issue under a source program.

**As a source section (Low Income), I issue and revoke** through `IGrantWrite`, passing only my source key.
- Grants resolves the open program for the key; none open is an error.
- Issuing to a current holder returns the existing grant.

**As a manager, I revoke a grant** with an optional reason, or **resend its notice**.
- The revoked row stays (who, when, why); it cannot be resent or revoked again.

**As a grant holder, I am told and can look it up.**
- On issue, resend and revoke I get an email in my language and an in-app notice; a failure in one channel does not stop the other or the write.
- My profile's "My grants" card lists my active grants and those that ended this year or later. Nobody else sees it.

**As UsersAdmin, Board or Admin, I see a person's grants** on their admin detail page, in every state.

## Data Model

`GrantProgram`, `GrantProgramManager`, `Grant`: see [Grants.md](../Grants.md#data-model). One live grant per person per program is a filtered unique index.

## Grant State

```
Active → Expired   (computed: the day after ExpiresOn ?? program.GrantsExpireOn, Europe/Madrid)
Active → Revoked   (manager, Board, Admin, or the source section)
```

Nothing stores or flips the state; it is read against today. Revoked wins over expired.

## Related

- Invariants, access rules, triggers: [Grants.md](../Grants.md)
- The first source: [LowIncome.md](../../../Humans.LowIncome/Docs/LowIncome.md)
- What it replaces: [Campaigns](../../../Humans.Campaigns/Docs/features/Campaigns-feature.md)
