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

Count the review-round commits already spent on this PR. Each review event is a separate
wake, hours after the last, and the context that would have told you *this is round
seventeen* has been compacted away by the time you read this. Derive the number from the
PR — it is the only thing in the loop that remembers:

    gh pr view <N> --repo <owner>/Humans --json commits \
      --jq '[.commits[] | select((.messageBody // "") | test("(?m)^Review-round: [0-9]+"))] | length'

Always pass `--repo` with the owner the PR actually lives on — fork and upstream reuse
PR numbers ([`issue-refs-qualified`](../../../memory/process/issue-refs-qualified.md)),
and a bare call reads the ambient clone's same-numbered PR instead.

**Only review rounds count.** A round is a commit pushed in response to an automated
review finding or a CI failure, from the point the PR's own work is functionally
complete, and it carries a `Review-round: <n>` trailer saying so. Commits that finish
what the PR was opened to deliver, commits answering Peter's own instructions (a
section-doctor run applying his Needs-Peter answers included), and mechanical commits —
rebase, base-branch merge, conflict resolution — are not rounds, carry no trailer, and
spend nothing. Nor do they reset or raise the ceiling: five bot rounds stay five. The
trailer is the memory, not the definition — a round commit that lost its trailer to a
rebase still counts, and where trailers and the PR's review history disagree, the history
wins ([`review-round-budget`](../../../memory/process/review-round-budget.md)).

**When the count is ambiguous, judge it by what the cap is for.** The number exists to
answer one question: *how many times has this PR already churned on automated review?*
Past about five, the next commit is likelier to open a leak than close one — that is the
whole reason for a ceiling, and it is what the trailers are trying to remember on your
behalf.

So a zero on a PR with a long review history is not a fresh budget, it is a missing
record. Sanity-check it against whether the PR has been reviewed at all:

    gh api repos/<owner>/Humans/pulls/<N>/reviews --paginate \
      --jq '[.[] | select(.user.type=="Bot" or (.user.login|test("codex|claude|gemini";"i")))] | length'

Non-zero there while the trailers say fewer rounds than that has plausibly drawn means the
trailers are behind: count the round commits yourself and use that. A red check earlier on
the PR says the same thing — CI answers are rounds too, and a PR can have spent several
before any bot reviewed it, so start from the first review *or* failure, whichever came
first. The same question settles the cases no rule here lists — a commit that both
finishes the deliverable and answers a finding, a round you had to push twice, a finding
answered by reverting. Ask what the commit was churning on, not what it touched, and act on
the answer instead of looking for a rule that names it.

The unattended ceiling is **five review-round commits per PR**. Then act on where you stand:

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

**One round is one commit.** Batch the round's fixes, and give the commit its
`Review-round: <n>` trailer. The count is trailered commits, and splitting a round across
three of them spends three rounds to do one.

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
- Raise the five-round ceiling, or push past it, on your own judgement. Peter says so
  first, in his own words — an instruction of his on some other part of the PR is not that.
- Put a `Review-round:` trailer on a commit that is not a round, or leave it off one that is.
- Skip, disable or quarantine a test to get CI green.
- Push a commit without `dotnet build Humans.slnx -v quiet` clean and
  `dotnet test Humans.slnx -v quiet` green.
