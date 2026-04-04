# 35 — Dietary & Medical Nudge Modal (Placeholder)

## Status: Not Yet Specced

## Business Context

The cantina feeds humans who work shifts of 6+ hours. Dietary preferences, allergies, intolerances, and medical conditions are only relevant once someone has a qualifying shift — not during initial preference setup.

This feature will present a modal nudge (triggered from a dashboard card) when a human has signed up for a qualifying shift but hasn't filled out their dietary/medical info.

## Scope (Rough)

- Dashboard card: "We see you have a shift — tell us about your food needs"
- Modal flow collecting: dietary preference, allergies (with "Other" text), intolerances (with "Other" text), medical conditions
- Triggered when: human has a confirmed signup on a 6+ hour shift AND dietary fields are empty
- Data stores into existing `VolunteerEventProfile` fields (already in schema)
- Medical conditions visibility restricted to owner, NoInfo Admins, and Admins (existing rule)

## Dependencies

- Feature 33 (Shift Preference Wizard) removes dietary/medical from `/Profile/ShiftInfo`
- Shift signup system (Feature 25) provides the qualifying shift trigger

## Related Features

- [33 — Shift Preference Wizard](33-shift-preference-wizard.md): removes dietary/medical from the preference page
- [25 — Shift Management](25-shift-management.md): shift signup data
- [Issue #273](https://github.com/nobodies-collective/Humans/issues/273): Dashboard "Things to do" card pattern
