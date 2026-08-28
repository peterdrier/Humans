# Ranked-choice voting

## Business context

The 2027 event-date decision needs one authenticated ballot per eligible
Asociado. Similar dates should be allowed to tie, dates may be explicitly
unacceptable, and dates that later prove unavailable must be removable from the
count without rewriting ballots.

This feature belongs to Surveys. It is not the formal bylaw/quorum voting system
tracked by nobodies-collective/Humans#86.

## V1 decisions

- A survey containing RankedChoice questions contains no other question types.
- Ranked ballots are Identified only.
- Equal ranks are enabled by default.
- Reject is optional and means unacceptable, not vetoed.
- Preference tiers are ranked > unranked > rejected.
- Ranked Pairs (Tideman) is the precommitted official method.
- Condorcet check and Borda are post-close sensitivity analysis.
- IRV, Baldwin, and Coombs are deferred.
- Authored option order is the disclosed final exact tie-break.
- Results are embargoed until the survey closes.
- A closed RankedChoice survey cannot reopen.

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

## Result embargo

While Open, Board/Admin may see participation only: eligible, started,
completed, outstanding, response rate, and reminder status. No answer-derived
data is available through results pages, exports, Backdoor endpoints,
raw-response views, or respondent drill-down.

This is a survey-level lifecycle rule, which is why v1 prohibits mixing ranked
and non-ranked questions. The builder explains the embargo and no-reopen rule
when RankedChoice is selected.

## Data model

Planned question settings:

- allow equal ranks;
- allow Reject;
- official method;
- unavailable option values.

Planned answer data:

- ordered rank groups of stable option values;
- a distinct rejected-option set.

Counting-affecting authoring state freezes after the first saved answer,
including a draft/autosave.

## Related

- [Survey section invariants](../Surveys.md)
- nobodies-collective/Humans#1151
