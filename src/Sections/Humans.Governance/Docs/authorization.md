# Governance — Authorization

| Controller | Scope | Roles | Source |
|---|---|---|---|
| `GovernanceController` | Class | `[Authorize]` (authenticated) | — |
| `GovernanceApplicationsController` | Class | `[Authorize]` (authenticated) | — |
| `GovernanceApplicationsController.Admin` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GovernanceApplicationsController.AdminDetail` | Action | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GovernanceBoardVotingController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` |
| `GovernanceBoardVotingController.Vote` | Action | `Board` | `PolicyNames.BoardOnly` |
| `GovernanceBoardVotingController.Finalize` | Action | `Admin` | `PolicyNames.AdminOnly` (POST `/Governance/BoardVoting/Finalize`; action-level override on class-level `BoardOrAdmin` — finalizes a board vote round by recording the meeting date and triggering application decisions) |
| `GovernanceVotesController` | Class | `[Authorize]` (authenticated) | — (every logged-in member sees every vote; only roster membership gates casting, and that is enforced in `IAssemblyVoteService`, not by a policy) |
| `GovernanceVotesAdminController` | Class | `Board, Admin` | `PolicyNames.BoardOrAdmin` (drafting, the admin list, and the post-close ballots list) |
| `GovernanceVotesAdminController.Open` | Action | `Admin` | `PolicyNames.AdminOnly` (action-level override; Admin-only for the first votes per [`features/assembly-votes.md`](features/assembly-votes.md) decision 1, then moves to `BoardOrAdmin`) |
| `GovernanceVotesAdminController.Stop` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GovernanceVotesAdminController.Extend` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GovernanceVotesAdminController.Cancel` | Action | `Admin` | `PolicyNames.AdminOnly` |
| `GovernanceVotesAdminController.Peek` | Action | `Admin` | `PolicyNames.AdminOnly` (reads the embargoed tally; every call writes an audit entry and a peek row published on the results page) |

No standalone `BoardController` — board-only actions live under `GovernanceBoardVotingController` above.
