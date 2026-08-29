# Ranked-choice voting

## Business context

The 2027 event-date decision needs one authenticated ballot per eligible
Asociado. Similar dates should be allowed to tie, dates may be explicitly
unacceptable, and dates that later prove unavailable must be removable from the
count without rewriting ballots.

This feature belongs to Surveys. It is not the formal bylaw/quorum voting system
tracked by nobodies-collective/Humans#86.

## V1 decisions

- RankedChoice is available in ordinary surveys as well as surveys marked
  **Asociado vote**.
- An Asociado vote is Identified-only and may contain mixed question types.
- An Asociado vote always targets the current active Asociados audience; current
  eligibility is still rechecked when each invitee answers and submits.
- Equal ranks are enabled by default.
- Reject is optional and means unacceptable, not vetoed.
- Preference tiers are ranked > unranked > rejected.
- Ranked Pairs (Tideman) is the precommitted official method.
- Condorcet check and Borda are post-close sensitivity analysis.
- IRV, Baldwin, and Coombs are deferred.
- Authored option order is the disclosed final exact tie-break.
- Every question's answer-derived results are embargoed until an Asociado vote
  closes; ordinary surveys retain live results.
- A closed Asociado vote cannot reopen.
- The entire Asociado-vote definition and audience are locked once the survey
  opens; post-close ranked-option availability remains the only mutable recount
  input.
- Eligibility is checked against current active, approved Asociado status at
  entry, while answering, and again at final submission.

## Counting

Pairwise comparison records strict preferences only. Equal-ranked, equally
unranked, and equally rejected options contribute no preference; indifference
is not half a vote.

Ranked Pairs sorts victories by:

1. descending margin;
2. descending winning votes;
3. winning option authored position;
4. losing option authored position.

It locks each victory unless the edge creates a cycle. Condorcet check reports a
candidate that strictly defeats every other active candidate, or the smallest
visible cycle. Borda assigns `m-1` through `0` points and averages the occupied
positions for every tied tier, including distinct unranked and rejected tiers.
Exact Borda totals use rational arithmetic.

## Availability

Authored candidates, cast ballots, and post-vote availability are separate
state. Excluding an unavailable option removes it from each ballot for the
recount and compresses remaining ranks. The stored ballot is unchanged.
Availability changes are reversible and audited. Closed results preserve both
the all-options outcome and the current available-only outcome.

When a head-to-head preference cycle is present, the results page names the
cycle and explains that removing an unavailable option can break it and change
the winner even when the previous winner remains available. The explanation
links to the public voting-method note at
`https://nobodies.team/event-dates-2027-voting-method.html#preference-cycles-and-availability`.

## Result embargo

Ordinary surveys keep the existing default: anyone authorized to use Surveys
results may see them while Open.

While an Asociado vote is Open, Board/Admin may see participation only:
eligible, started, completed, outstanding, response rate, and reminder status.
No answer-derived data from any question type is available through results
pages, exports, Backdoor endpoints, raw-response views, or respondent
drill-down.

The builder presents Asociado-vote mode as a separate binding-vote section and
explains the Identified-only, eligibility, whole-definition lock, embargo, and
no-reopen consequences before opening. Respondents see a binding-vote notice on
the intro and question pages. RankedChoice requires that mode.

## Data model

Planned question settings:

- allow equal ranks;
- allow Reject;
- official method;
- unavailable option values.

Planned survey setting:

- whether the survey is an Asociado vote.

Planned answer data:

- ordered rank groups of stable option values;
- a distinct rejected-option set.

Counting-affecting authoring state freezes after the first saved answer,
including a draft/autosave. The builder visibly disables the ranked settings
at that point instead of allowing an edit that the server will reject.

## Related

- [Survey section invariants](../Surveys.md)
- nobodies-collective/Humans#1151
