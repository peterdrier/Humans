---
name: Localization — admin pages do not require it
description: Existing `@Localizer[...]` calls in admin views can stay, but don't add new resource keys for admin-side views (`/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`). Only public/user-facing views require localization.
---

<!-- freshness:triggers
  src/Humans.Web/Views/Admin
  src/Humans.Web/Views/TeamAdmin
  src/Humans.Web/Controllers/ShiftDashboardController.cs
-->
<!-- freshness:flag-on-change
  Flag if the Admin/TeamAdmin view folders or ShiftDashboardController are renamed/removed.
-->

**Admin pages do not require localization.** Existing localized strings in admin views can stay, but do not add new `@Localizer[...]` calls or resource keys for admin-side views (`/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`) until further notice. Only public/user-facing views require localization.

The coordinator-facing `/Shifts/Dashboard` is an admin function — existing localization can stay, but new strings there do **not** need to be added to `ca`/`de`/`fr`/`it` resources.
