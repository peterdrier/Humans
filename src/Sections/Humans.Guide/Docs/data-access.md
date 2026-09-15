# Guide — Data Access

## Guide

Project: `src/Sections/Humans.Guide` — services under `Services/`. **No
DbContext, no repository, no tables:** the section renders Markdown guide
content sourced from the repository tree, so its whole data footprint is the
in-memory segment cache.

### GuideContentService (Singleton)

No repository. `GetPageAsync(fileStem, roleContext)` reads the page's
`GuideDocument` — its ordered role-scoped segments — from `IMemoryCache`,
populating a miss by fetching Markdown through `IGuideContentSource` and
segmenting it with `GuideSegmenter`; entry TTL comes from
`IOptions<GuideSettings>`. Rendering is not cached: the segments the caller
may see are filtered and then rendered via `IGuideRenderer` per request.
`RefreshAllAsync` re-fetches and re-segments every stem in `GuideFiles.All`,
cached or not; a stem whose fetch fails keeps the copy already cached. No DB
access.

---
