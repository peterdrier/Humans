# Scanner — Gate Terminal Login

## Business Context

The laptop at gate uses the ticket lookup tool (`/Scanner/Tickets`) to check
people's ticket status and early-entry date as they arrive. The device is shared
between whoever is on gate shift, so tying it to one human's login is wrong: their
session, their personal permissions, their magic-link email. And the previous
access policy (`TicketAdminBoardOrAdmin`) meant a gate laptop could only be signed
in by handing it ticket-admin powers — vendor syncs, exports, transfer decisions.

The fix is a **shared gate-terminal account**: a real `User` row with a well-known
GUID (`SystemUserIds.GateTerminal`, reserved block `0004`), akin to the dev-login
personas. It is not a person — no email, no roles, no team memberships. Ticket
admins set its password from the ticketing admin pages; gate staff sign the laptop
in once and the session persists across restarts.

**Read-only by construction, not by enforcement.** The account holds no admin
roles, so every admin surface and private-info page stays invisible. Self-scoped
writes are the same writes any baseline volunteer could do, scoped to the gate
account itself. No per-controller read-only machinery was added (deliberate — see
the design dialogue: "no heroics on the hard read-only bit").

## How It Works

- **Account:** provisioned lazily the first time a ticket admin sets the password
  (`GateTerminalAccountSeeder`). User row with the well-known GUID + a minimal
  Profile ("Gate Terminal", legal name Gate/Terminal) created through the canonical
  `SaveProfileAsync` path so `UserStateClassifier` yields `Active` (the membership
  filter requires it). Consent check is auto-cleared so the account never sits in
  the Consent Coordinator review queue.
- **Credential:** the Identity `PasswordHash` on the row itself — no extra storage,
  no schema change. Set/rotate at `/Tickets/Admin/Gate` (policy `TicketAdminOrAdmin`).
  Rotation bumps the security stamp, which kills existing gate sessions at the next
  security-stamp validation sweep (≤30 min), and clears any lockout.
- **Login:** `/Account/GateLogin` (anonymous GET form + POST). Username is the fixed
  constant `gate`; password checked via `CheckPasswordSignInAsync` with
  `lockoutOnFailure: true` (Identity lockout = brute-force protection that self-heals
  in minutes). Success signs in with `isPersistent: true` and redirects to
  `/Scanner/Tickets`.
- **Authorization:** scanner routes moved from `TicketAdminBoardOrAdmin` to the new
  `ScannerAccess` policy — TicketAdmin/Board/Admin roles OR `NameIdentifier ==
  SystemUserIds.GateTerminal`. Deliberately NOT a `RoleNames` constant: role
  constants flow into the role-assignment UI and dev personas via the test-enforced
  `RoleNames.All`.
- **Audit:** every password set/rotate writes `AuditAction.GateTerminalPasswordSet`
  with the acting admin as actor. Gate sign-ins stamp `LastLoginAt` (surfaced on the
  admin card).

## Scope & Non-Goals

### In Scope

- `SystemUserIds` constants + GUID block `0004` reservation.
- `ScannerAccess` policy; `ScannerController` switched to it.
- `/Account/GateLogin` (localized, all six locales).
- `/Tickets/Admin/Gate` admin card (status + set/rotate password) + admin-nav entry.
- `GateTerminalAccountSeeder` (Web infrastructure, registered in all environments).

### Out of Scope

- Check-in / attendance marking — Scanner remains read-only (section invariant).
- Any ticket-admin page access for the gate account (`/Tickets/*` still requires
  the ticket-admin policies).
- Per-controller read-only enforcement for the gate session.
- Multiple terminal accounts / per-device credentials.

## Operational Notes

- If the laptop loses its cookie (cleared browser, >14 days idle), gate staff
  re-enter the known credential at `/Account/GateLogin` — no admin needed on-site.
- Wrong password 5× locks the account for the Identity default window (~5 min);
  the admin card shows lockout state, and setting a new password clears it.
- The account shows up as "Gate Terminal" in people lists/search (it has a
  profile); that's accepted and self-documenting.
