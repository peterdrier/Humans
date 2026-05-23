# Reuse Review Rules

Reuse review answers one question: **did this PR add durable surface that should have reused existing code instead?** It is separate from spec review and correctness review.

Run it after implementation/spec review and before code review. Findings are actionable only when they name the existing surface that should have been reused, or the new surface whose long-term cost is not justified.

## Review Checklist

- **New files:** Does each new file have a clear owner, or could it live in an existing file/component without making that owner incoherent?
- **Public types:** Does each new `public` type need to be public? Could an existing type carry the field/behavior?
- **Interfaces:** Any added interface method must satisfy `memory/architecture/interface-method-additions-are-debt.md`.
- **Services/repositories:** Did the PR add a parallel service/repository/helper where an owning service already exists?
- **DTOs/view models:** Is the new shape reused by multiple callers, or is it a one-off wrapper around an existing model?
- **Endpoints/pages:** Is this a new workflow surface, or should it be an action/view under an existing route owner?
- **DI/dependencies:** Is every new registration/package necessary, or did it bypass an existing project helper?
- **Net shape:** For cleanup/refactor PRs, is the diff net-neutral or net-negative in durable surface? If not, is the reason explicit?

## Finding Format

```text
REUSE — <new surface> should reuse <existing surface>.
Why: <long-term cost or duplicate ownership>.
Fix: <specific consolidation or caller-side composition>.
```

## Non-Findings

Do not flag new surface that is directly required by an accepted feature spec, an EF migration scaffold, generated code, or a clear section boundary. Do not flag a local variable, private helper, or one-call-site mapping block merely because it could be abstracted.
