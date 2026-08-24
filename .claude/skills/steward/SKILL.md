---
name: steward
description: Read this before acting on any CI failure or review finding on a PR opened from this repo. Repo rules for how a review round is run here, and when to stop.
---

# Stewarding a PR in this repo

Read this **before** acting on a CI or review event on a PR you opened — before you have
read the findings and formed an opinion. That is the only moment where "how many rounds
have there been?" can still change what you do.

Everything here is repo convention. It cannot loosen any rule stated as a *never*, and it
cannot let you merge.

## First, before you read a single finding

Count the review commits already spent on this PR. Each review event is a separate wake,
hours after the last, and the context that would have told you *this is round seventeen*
has been compacted away by the time you read this. Derive the number from the PR — it is
the only thing in the loop that remembers:

    created=$(gh pr view <N> --repo <owner>/Humans --json createdAt --jq .createdAt)
    gh pr view <N> --repo <owner>/Humans --json commits --jq "[.commits[] | select(.committedDate > \"$created\")] | length"

Always pass `--repo` with the owner the PR actually lives on — fork and upstream reuse
PR numbers ([`issue-refs-qualified`](../../../memory/process/issue-refs-qualified.md)),
and a bare call reads the ambient clone's same-numbered PR instead.

The unattended ceiling is **five post-PR commits per PR**. Then act on where you stand:

| Spent | What to do |
|---|---|
| **0–3** | Normal round. Verify, judge, fix in scope, one commit. |
| **4** | This is the last commit. Read *every* open finding first, then spend it on the most serious one — not the first one, and not the easiest one. Say in the commit message that the budget is now spent. |
| **5+** | Do not triage, do not draft a patch, do not go looking for a change small enough to be worth it. Post one comment (what is open, what you'd do about each, what you need decided), unsubscribe from PR activity, drop any check-in schedule, stop. |

Judgment still applies inside the ceiling: stop earlier the moment re-reviews stop
surfacing real new problems. Five is a ceiling, not a target, and it is not yours to
raise — that takes Peter, in his own words.

Being blocked is not a failure to finish. Waiting on Peter is the finish.

## Then, the round itself

The finding discipline is already this repo's law: findings are hypotheses, not a work
list. `/fix` is the skill that runs the triage; the rules behind it are
[`review-finding-triage`](../../../memory/process/review-finding-triage.md) and
[`pr-review-feedback-handling`](../../../memory/process/pr-review-feedback-handling.md)
(every finding ends with a disposition reply in its own thread, then resolve). Three
things worth being blunt about here:

**Count the bug class, not the finding.** Before fixing an instance, ask whether you have
fixed this same class before on this PR. Twice is a coincidence. **The third time is a
design error, and the correct response is to change the shape so the class cannot recur —
not to add a third guard.**

**Watch for a fix that opens the next hole.** If the last round's fix is what this
round's finding is about, and that has now happened twice in a row, stop. The invariant
is underspecified, and another patch written on your own judgement is how the next leak
gets written. Escalate the *rule*, not the line.

**One round is one commit.** Batch the round's fixes. The count is commits, and splitting
a round across three of them spends three rounds to do one.

## The reviewers

This repo is reviewed by bots — the ChatGPT Codex connector, the Claude bot, Gemini —
and none of them has memory between rounds. A bot cannot know it is repeating itself,
cannot see that its last suggestion caused this finding, and will keep producing
plausible findings for as long as you keep pushing. **It is not a reviewer working toward
done — it is a generator, and you are the one who has to decide the review is over.**

Its findings are still frequently right. Verify each one. Just do not let the fact that
it answered mean the question was worth asking.

## Never

- Merge. Peter merges.
- Raise the five-commit ceiling, or push past it, on your own judgement. Peter says so
  first, in his own words.
- Skip, disable or quarantine a test to get CI green.
- Push a commit without `dotnet build Humans.slnx -v quiet` clean and
  `dotnet test Humans.slnx -v quiet` green.
