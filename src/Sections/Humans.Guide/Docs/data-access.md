# Guide — Data Access

## Guide

Project: `src/Sections/Humans.Guide` — services under `Services/`. **No
DbContext, no repository, no tables:** the section renders Markdown guide
content sourced from the repository tree, so its whole data footprint is the
in-memory render cache.

### GuideContentService (Singleton)

No repository. `GetRenderedAsync(fileStem)` serves rendered guide HTML from
`IMemoryCache`, populating a miss by fetching Markdown through
`IGuideContentSource` and rendering it via `IGuideRenderer`; entry TTL comes
from `IOptions<GuideSettings>`. `RefreshAllAsync` re-fetches and re-renders
every stem in `GuideFiles.All`, cached or not; a stem whose fetch fails keeps
the copy already cached. No DB access.

---
