---
name: Always log problems — even expected ones, at Warning, without the exception
description: Expected/user-driven problems still get logged at LogWarning. Drop the exception object if the stack trace is noise, but keep the severity. Information is invisible in prod.
---

Always log problems. If a condition is a "known problem that happens in the normal course of action" (user-input validation failures, guardrail violations like "can't delete shift with signups", client-aborted requests), that doesn't mean "don't log it" — it means "log it at Warning without the exception object." Drop the stack trace, drop the `ex` argument, keep `LogWarning`. The event still needs to be in the logs AND visible in the prod log viewer.

**Why severity stays at Warning:** The production log viewer only renders Warning and above. `LogInformation` and `LogDebug` are effectively invisible in prod — downgrading is the same as deletion for observability. Peter pushed back on PR #226 twice: first for deleting the log calls entirely (for #497/#499/#500), then for downgrading to `LogInformation` (which hid them from the prod viewer). Keep Warning, drop the exception object.

**Why we log at all:** Incident investigation, usage patterns, abuse detection, and "is this happening more than usual?" all depend on a record of problems even when mundane. Dropping the log line or making it invisible in prod both destroy that signal.

**How to apply:**

When a catch block handles an expected / user-driven exception, rewrite the log call to drop the exception object but stay at Warning:

```csharp
// Bad (spams stack trace for expected condition):
_logger.LogWarning(ex, "Failed to add email address for user {UserId}", user.Id);

// Bad (invisible in prod):
_logger.LogInformation("Rejected email for user {UserId}: {Reason}", user.Id, ex.Message);

// Good (visible, no stack):
_logger.LogWarning("Rejected email add for user {UserId}: {Reason}", user.Id, ex.Message);
```

Only a truly no-op event (successful no-op sync tick with nothing to do) can skip logging entirely.

If a sprint plan or issue says "remove the log warning", read it as "remove the *exception argument* from the log warning" — ask if unsure. **Never delete the call, never downgrade below Warning.**
