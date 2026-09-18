# Notifications API

`GET /api/notifications` exposes one configured human's unread notification inbox and live
meters to an operator-managed external consumer. It is read-only: polling never marks a row
read, dismisses it, or resolves it.

## Configuration

Set both values under `NotificationApi` (environment variables use
`NotificationApi__ApiKey` and `NotificationApi__UserId`):

- `ApiKey` — the secret presented in the `X-Api-Key` request header.
- `UserId` — the Humans user whose inbox and role-scoped meters the API returns.

The endpoint returns `401 Unauthorized` when the header is missing or wrong, either setting is
empty or invalid, the configured user does not exist, or the configured user is a tombstone.

## Request

```http
GET /api/notifications
X-Api-Key: configured-secret
```

No cookie or anti-forgery token is required. There are no write endpoints.

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

Notifications are newest first. Meters use the configured user's active roles and are ordered
by priority. Empty collections are returned as `[]`.
