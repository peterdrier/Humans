<!-- freshness:triggers
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Controllers/TicketsOnsiteAdminController.cs
  src/Humans.Web/Views/Account/GateLogin.cshtml
  src/Humans.Web/Views/Tickets/Admin/Gate.cshtml
  src/Humans.Web/Infrastructure/GateTerminalAccountSeeder.cs
  src/Humans.Web/Infrastructure/GateLoginThrottle.cs
  src/Humans.Domain/Constants/SystemUserIds.cs
-->
<!-- freshness:flag-on-change
  Gate-terminal shared account (GateTerminal GUID, ScannerAccess policy, /Account/GateLogin, /Tickets/Admin/Gate password set/rotate, GateTerminalAccountSeeder, GateLoginThrottle, onsite-roster access) — review when any of these change.
-->

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
  no schema change. Set/rotate at `/Tickets/Admin/Gate` (policy `TicketAdminOrAdmin`)
  via reset-token replace, so a new password that fails the Identity policy leaves the
  old password untouched. A successful rotation bumps the security stamp, which kills
  existing gate sessions at the next security-stamp validation sweep (≤30 min).
- **Login:** `/Account/GateLogin` (anonymous GET form + POST). Username is the fixed
  constant `gate`; password checked via `CheckPasswordSignInAsync` with
  `lockoutOnFailure: false`. Success signs in with `isPersistent: true` and redirects
  to `/Scanner/Tickets`.
- **Brute-force protection is per source IP, never per account.** The username is
  public, so Identity's per-account lockout would let anyone deny the real terminal
  at gate by deliberately failing passwords — the attacker and the victim would share
  the lockout. Instead `GateLoginThrottle` (in-memory, singleton) allows 10 failed
  attempts per minute per source IP; an attacker only ever locks themselves out.
  Lockout is explicitly disabled on the account (`SetLockoutEnabledAsync(false)` on
  every password set) so no other code path can ever lock it either. Forwarded
  headers are enabled in deployment, so `RemoteIpAddress` is the real client IP.
  Known trade-off: an attacker NATed behind the same public IP as the gate laptop
  (event wifi) shares its bucket for the 60-second window — accepted, and the error
  message tells gate staff exactly that.
- **Errors are actionable, never blank.** Wrong credential: says the username is
  `gate` and the password comes from the ticket team. Throttled: says sign-in from
  this network is paused for {N} seconds, why (too many wrong-password attempts from
  this connection — possibly someone else on it), what to do (wait, use the correct
  password, ask the ticket team), and that the account itself is NOT locked and
  admins can verify/change the password under Tickets → Gate terminal.
- **Authorization:** scanner routes and the onsite-roster route moved from
  `TicketAdminBoardOrAdmin` to the new `ScannerAccess` policy — TicketAdmin/Board/Admin
  roles OR `NameIdentifier == SystemUserIds.GateTerminal`. This means the gate terminal
  can also reach `/Tickets/Admin/Onsite` (the who's-onsite roster) directly from the
  door, without any separate login. Deliberately NOT a `RoleNames` constant: role
  constants flow into the role-assignment UI and dev personas via the test-enforced
  `RoleNames.All`.
- **Audit:** every password set/rotate writes `AuditAction.GateTerminalPasswordSet`
  with the acting admin as actor. Gate sign-ins stamp `LastLoginAt` (surfaced on the
  admin card).

## Scope & Non-Goals

### In Scope

- `SystemUserIds` constants + GUID block `0004` reservation.
- `ScannerAccess` policy; `ScannerController` and `TicketsOnsiteAdminController` switched to it.
- `/Account/GateLogin` (localized, all six locales).
- `/Tickets/Admin/Gate` admin card (status + set/rotate password) + admin-nav entry.
- `GateTerminalAccountSeeder` (Web infrastructure, registered in all environments).
- Onsite roster (`/Tickets/Admin/Onsite`) accessible to the gate terminal via `ScannerAccess`.

### Out of Scope

- Check-in / attendance marking — Scanner remains read-only (section invariant).
- Any ticket-admin page access for the gate account (`/Tickets/*` still requires
  the ticket-admin policies).
- Per-controller read-only enforcement for the gate session.
- Multiple terminal accounts / per-device credentials.

## Operational Notes

- If the laptop loses its cookie (cleared browser, >14 days idle), gate staff
  re-enter the known credential at `/Account/GateLogin` — no admin needed on-site.
- The account can never be locked out. Sustained wrong passwords from one source
  pause that source for 60-second windows; the gate laptop on its own IP is
  unaffected by attackers elsewhere, and the on-screen error explains the wait
  and the fix.
- The account shows up as "Gate Terminal" in people lists/search (it has a
  profile); that's accepted and self-documenting.
