# Auth — Language

Signing in and system roles. Settled in the 2026-11-24 terminology grill. Shared words live in the root [`CONTEXT.md`](../../../../CONTEXT.md).

## Signing in

**Sign in / sign out**:
Starting and ending a session in Humans, by Google or by sign-in link. The noun is "sign-in" ("Last sign-in").
_Avoid_: log in, login, log out, logout

**Sign up**:
Creating a Humans account.
_Avoid_: register (Register means camp registration), create account

**Sign-in link**:
The one-use link we email so someone can sign in, or sign up, without a password.
_Avoid_: magic link, login link

## System roles

**System role**:
A role that unlocks a whole section or the whole app: **Admin**, **Board**, and one admin per section (TeamsAdmin, CampsAdmin and so on). Everything else is a **role** from the Roles section, which belongs to a team or camp. One exception: FinanceAdmin runs Budget, Finance and Holded together.
_Avoid_: role (alone), role assignment

**Assign / revoke**:
Giving someone a system role, and taking it away again.
_Avoid_: end, remove, unassign

## Retiring

ShiftsNoInfo was retired on 2026-11-26 with no replacement yet; its rows grant nothing, and the `auth.end-retired-roles` data migration ends the active ones with an audit entry each. ConsentCoordinator was renamed ConsentAdmin, the Consent section's admin.
