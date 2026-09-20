# Notifications machine API

`GET /api/backdoor/notifications` returns the key owner's unread notification inbox and live
meters to an agent polling on their behalf. It is read-only: polling never marks a row read,
dismisses it, or resolves it.

The endpoint lives in Backdoor, like every key-authed API: a personal Backdoor key
(`X-Api-Key`, issued at `/Backdoor` to an Admin or Board member) authenticates as its owner,
and a missing, unknown or revoked key is a 401. Notifications contributes only the read
surface, `INotificationInboxRead` (`Contracts/`). Key lifecycle and eligibility are
documented in [Backdoor](../../../Humans.Backdoor/Docs/Backdoor.md).

## Request

```http
GET /api/backdoor/notifications
X-Api-Key: <personal key>
```

## Response

```json
{
  "notifications": [
    {
      "date": "2026-04-10T09:00:00Z",
      "source": "ConsentReviewNeeded",
      "subject": "Consent review pending: Alice",
      "link": "/OnboardingReview",
      "priority": "high",
      "class": "actionable",
      "unread": true
    }
  ],
  "meters": [
    {
      "label": "Applications pending your vote",
      "count": 5,
      "link": "/Governance/BoardVoting"
    }
  ]
}
```

Notifications are the owner's unread, unresolved rows, newest first. Meters follow the
owner's active roles and are ordered by priority. Empty collections are returned as `[]`.
