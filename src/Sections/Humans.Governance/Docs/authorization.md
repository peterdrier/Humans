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

No standalone `BoardController` — board-only actions live under `GovernanceBoardVotingController` above.
