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
A role that unlocks a whole section or the whole app: **Admin**, **Board**, and one admin per section (TeamsAdmin, CampsAdmin and so on). Everything else is a **role** from the Roles section, which belongs to a team or camp.
_Avoid_: role (alone), role assignment

**Assign / revoke**:
Giving someone a system role, and taking it away again.
_Avoid_: end, remove, unassign

## Retiring

ConsentCoordinator and ShiftsNoInfo are legacy system roles being retired: their access moves to the owning section's admin or to Roles-section roles. Don't build on them.
