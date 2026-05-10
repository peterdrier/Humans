# TT changelog + community recheck for Option A (#382 follow-up)

## Question

Has anything been shipped or documented (officially or in the wild) that would let us update an issued ticket's attendee name/email without void+reissue?

## Verdict

**Confirmed-no-Option-A.** No changelog entry, no community report, no client library, and no third-party catalog surfaces an update/PATCH operation on `issued_tickets`; the conspicuous absence of "update" from TT's own MCP-tools listing for Issued Tickets — while explicitly listing it for Issued Memberships in the same page — is a strong corroborating negative signal.

## What I checked

### 1. Changelog / release notes

- `developers.tickettailor.com/changelog`, `/release-notes`, `/whats-new` all return **HTTP 404**. TT does not publish a changelog under the developer subdomain.
- `tickettailor.com/blog/our-latest-product-improvements-api-endpoints-...` (the closest thing to a public release-notes post on the marketing blog) is firewalled (403) but its title and search snippets show only generic "API endpoints, basket timeout speeds" announcements with no mention of attendee update or transfer.
- Older blog post `our-beta-api-has-launched` is also firewalled (403); no search snippet referenced an update endpoint.
- **Result:** no changelog evidence of an attendee-update or ticket-transfer endpoint shipped in the last 12-18 months.

### 2. Community / forum / support threads

- Web searches for `tickettailor api "update issued ticket"`, `"change attendee name"`, `"transfer ticket"`, `PATCH issued_tickets undocumented`, `site:reddit.com tickettailor api transfer attendee`, `site:stackoverflow.com tickettailor api ticket attendee`, and TT support/help-centre searches for transfer/attendee returned **zero direct hits** — no developer reports of an undocumented working endpoint, no TT support staff confirming or denying the capability in any indexed public thread.
- The only on-topic help-centre article (`how-can-i-edit-an-order`) returned 403 to WebFetch, but its search snippet only references the **dashboard** edit-order flow ("change the name, email or other customer details from 'Orders' > 'Edit order' in the UI"). No mention of API parity.
- **Result:** silence everywhere. Either the capability does not exist over the public API, or no one is talking about it publicly.

### 3. API client libraries / dashboard reverse-engineering

- **`webdevhayes/laravel-ticket-tailor-wrapper`** (active, last release Feb 2026) — reviewed `src/LaravelTicketTailorWrapper.php` directly. Surface is **read-only**: `getAllIssuedTickets`, `getSingleIssuedTicket`, `getAllEvents`, `getSingleEvent`, `getAllOrders`, `getSingleOrder`. **Zero update/PATCH/POST methods.** No attendee-related methods.
- **`dbt-labs/tap-tickettailor`** (Singer tap) — extraction-only catalog of Events / Issued Tickets / Orders. No write operations.
- **Pipedream and Ibexa Connect** integrations expose only triggers (new order, updated order webhook) and read actions (List/Get Orders, List/Get Events). Neither offers an "update issued ticket" or "edit attendee" action; both fall back to a generic "Make an API Call" escape hatch, which itself implies no first-class endpoint to wrap.
- **Rollout integration guide** explicitly states: *"Issued Tickets: Create an issued ticket and Void an issued ticket (no update capability documented)."* Confirms the void+reissue model from outside TT's own docs.
- **Most informative single source:** TT's own `developers.tickettailor.com/docs/mcp/available-tools/`. It enumerates per-resource capabilities and **explicitly lists "create, update, void, and manage" for Issued Memberships, but only "generate individual tickets" for Issued Tickets**. The asymmetry is deliberate: when TT has an update operation, they document it. They do not for issued tickets.
- The "Create an issued ticket" page lists sibling actions on the resource: List, Get, Create, **Void**, plus the two webhooks. No update sibling.
- No public reverse-engineering of the dashboard's edit-attendee XHR was discoverable. Inspecting it would require a TT box-office login, which is out of scope for this probe.

## If anything was found

Nothing on issued tickets. The only adjacent finding is `POST /v1/orders/:order_id` ("Update an order"), which is documented but its updatable-fields list is not published — search snippets and the page itself do not enumerate whether `buyer_details` (name/email) is mutable, and crucially this is an **order-level** mutation that has no documented effect on per-ticket attendee names. Pursuing this would require live-credential probing and is a strictly weaker option than void+reissue (we cannot verify it changes the name printed on the ticket without a live test).

## Recommendation

**Stick with Option B (void + reissue).** Three independent signals point the same way: (a) TT's own MCP-tools page lists update capability for Issued Memberships but not Issued Tickets — a deliberate omission; (b) every third-party client/catalog (Laravel wrapper, Singer tap, Pipedream, Ibexa, Rollout) implements only Create + Void on issued tickets; (c) zero developer-community reports of an undocumented working endpoint exist on Reddit, Stack Overflow, or GitHub. If Peter still wants final certainty, the cheapest definitive test is a live-credential probe against `PATCH /v1/issued_tickets/{id}` with a known issued ticket ID — but doing the void+reissue implementation in parallel is not blocked on that result.
