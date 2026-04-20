# 12_google_drive_access — sonnet

**Category:** google  
**Duration:** 8439 ms  
**Cost:** $0.0126  
**Tokens:** input=99, cache_read=27065, cache_write=0, output=278

## User context

```yaml
display_name: Marta
locale: en
tier: Volunteer
approved: true
roles:
- Volunteer
teams:
- Volunteers
- Build
pending_consent_docs: []
open_tickets: []
open_feedback: []
```

## Question

I joined the Build team but I don't see the Build shared drive in my Google Drive. What's wrong?

## Answer

Google Drive access for a team is provisioned via Google resource sync — when you joined Build, a sync event should have been queued to grant you folder permissions.

A few things that could explain the delay or gap:

1. **Sync hasn't run yet.** Google resource sync runs as a background job. It may not have processed your membership yet — give it a short while and check again.
2. **Sync is set to a restricted mode.** An Admin can configure the Build team's Google sync to `None` (disabled) or `AddOnly`. If it's set to `None`, no sync events are processed at all.
3. **No Google resource is linked.** The Build team needs a Drive folder explicitly linked to it by a TeamsAdmin or your department Coordinator before permissions can be granted.
4. **Your Google account.** Sync uses the Google service email on your profile. Make sure the Google account you're checking Drive with matches the one on your profile at `/Profile/Me`.

**Next steps:**
- Check your Google service email at `/Profile/Me`.
- If it looks correct and the issue persists after a reasonable wait, reach out to your Build team Coordinator or use the feedback widget to flag it for an Admin to check the sync status.
