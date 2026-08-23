---
name: Localization — admin pages do not require it
description: Existing `@Localizer[...]` calls in admin views can stay, but don't add new resource keys for admin-side views (`/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`). Only public/user-facing views require localization.
---

**Admin pages do not require localization.** Existing localized strings in admin views can stay, but do not add new `@Localizer[...]` calls or resource keys for admin-side views (`/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`) until further notice. Only public/user-facing views require localization.

The coordinator-facing `/Shifts/Dashboard` is an admin function — existing localization can stay, but new strings there do **not** need to be added to `ca`/`de`/`fr`/`it` resources.

This exemption list is load-bearing for `/section-doctor`'s localization-coverage detector
(`.claude/skills/section-doctor/loc-coverage.py`), which buckets views by route. Change the list
here and the detector changes with it.
