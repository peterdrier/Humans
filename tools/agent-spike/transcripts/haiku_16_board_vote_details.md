# 16_board_vote_details — haiku

**Category:** governance  
**Duration:** 5487 ms  
**Cost:** $0.0047  
**Tokens:** input=95, cache_read=27064, cache_write=0, output=381

## User context

```yaml
display_name: Isabel
locale: en
tier: Asociado
approved: true
roles:
- Volunteer
- Board
teams:
- Volunteers
- Board
pending_consent_docs: []
open_tickets: []
open_feedback: []
```

## Question

I'm on the Board. How does voting on a Colaborador application actually work?

## Answer

# Board Voting on Tier Applications

As a Board member, here's how the voting process works:

## The Flow

1. **Application submitted** — A human applies to become a Colaborador (or Asociado).
2. **You vote** — You and other Board members each cast one individual vote on the application. Each Board member gets exactly one vote per application.
3. **Finalization** — Once the application is approved or rejected (via Board decision), all individual vote records are deleted. Only the collective decision note and meeting date survive in the system.

## Key Points

- **Your vote is transient.** It's working data while the application is under review. Once the application is finalized (approved or rejected), your individual vote is permanently removed from the database — this is intentional for GDPR data minimization.
- **Decision note + meeting date stay.** The outcome and context are retained; the granular voting record is not.
- **Separate from onboarding.** Tier applications and Volunteer onboarding are parallel, independent paths. A Colaborador application never blocks someone from becoming a Volunteer.

## What Happens on Approval

When an application is approved:
- The human's membership tier is updated on their profile to Colaborador (or Asociado).
- They are automatically added to the Colaboradors or Asociados system team.
- Their term expiry is set to December 31 of an odd year that is at least 2 years from the approval date (e.g., approved in 2025 → expires Dec 31, 2027).

You can view pending applications and cast your vote on the Governance section of the app. Need more details about the application interface?
