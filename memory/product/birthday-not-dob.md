---
name: Storage and UI — "birthday" (month + day), never "date of birth"
description: The system stores birthday (month + day only), not date of birth (which implies year). Use "birthday" in all UI text.
---

The system stores **birthday** (month + day only), not **date of birth** (which implies year). Use "birthday" in UI text.

**Why:** "Date of birth" implies the system stores the birth year, which it does not. Using "DoB" or "date of birth" in UI text would imply a data point we deliberately don't collect.

**How to apply:**

- UI labels, form fields, profile sections, validation messages, emails: "birthday".
- Don't add a year field "for completeness" — the schema is intentional. Birthday celebrations on the dashboard work without it.
- Internal code can use whatever name fits (`BirthdayMonth`, `BirthdayDay`); the rule is about user-facing copy.
