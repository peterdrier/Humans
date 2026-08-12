# HUM0031 thresholds are frozen until 2027 — never propose changing them

`HUM0031` (`ControllerBusinessLogicAnalyzer`) fires at **> 40 statements** or **cyclomatic
complexity > 15** on a controller method. Those thresholds are **frozen at 40/15 until 2027**.

**Never** propose tightening them, unfreezing them, lowering them, expanding their scope, or
"re-evaluating now that #866 has progressed". Do not raise it as a question, a suggestion, an
open item in a sweep report, or a follow-up. Peter will lower them when he decides to, and does
not want to be asked again.

Report what the analyzer currently enforces if a doc needs it, then stop.

**Why:** the freeze is a deliberate product decision, not an oversight waiting to be noticed.
Agents kept re-surfacing it every time #866 (the G5 section split) advanced, because the old
wording tied the freeze to that issue. It is tied to a date, not to #866.

**How to apply:** when regenerating `docs/architecture/roslyn-analysis.md` or any analyzer
inventory, carry the 40/15 numbers and the 2027 date forward verbatim. If a doc still ties the
freeze to nobodies-collective/Humans#866, that wording is stale — correct it to 2027, and do not
turn the correction into a question about whether the freeze should end.

Related: [[grandfathered-attribute-is-debt]] · [[analyzer-over-test-for-call-site-rules]]
