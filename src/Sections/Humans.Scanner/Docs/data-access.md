# Scanner — Data Access

## Scanner

Folder: `src/Sections/Humans.Scanner/Controllers/`. No owned DB tables, no
service, no repository, no `DbContext`, no cache — the section is two camera
pages and the partial one of them renders, and every value on screen is read
through another section's public contract.

### ScannerController (Scoped)

The section's only class with dependencies, and none of them is a repository.
Cross-section reads via `ITicketServiceRead` (the barcode-to-attendee lookup),
`IUserServiceRead`, `IEarlyEntryService`, `IConsentServiceRead`,
`IICalFeedService`, `IEventServiceRead` and `IBurnSettingsService`. Every action
is a `GET` and none of them writes, so the section owns no invalidation: the
ticket card is assembled per request from those reads and held nowhere.

---
