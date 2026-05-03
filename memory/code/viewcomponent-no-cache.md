---
name: View components must not inject IMemoryCache
description: View components cannot reach past the service boundary to manage caching. The owning service exposes a cached accessor; the view component just calls it.
---

View components MUST NOT inject or use `IMemoryCache` directly. All caching of data a view component needs lives in the service layer that owns the data.

**Why:** Violates the "services own their data" rule in [`design-rules.md`](../../docs/architecture/design-rules.md). A view component that reads `IMemoryCache` has reached past the service boundary and is making ownership/eviction decisions about data it doesn't own. This creates cache consistency bugs (the owning service can't invalidate stale entries it didn't write) and hides data access patterns from the service layer. Past incident: PR #222 added `IMemoryCache` directly to `UserAvatarViewComponent` for avatar URL resolution; the fix moved avatar caching into `IProfileService`.

**How to apply:**

When a view component needs fast-access data (avatar URL, display name, etc.), the owning service exposes a cached accessor. The view component calls the service; the service handles caching internally. If the service doesn't yet cache the relevant data, add the caching there — not in the view component.
