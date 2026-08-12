---
name: Razor — escape `@` in `<script src>` URLs as `&#64;`, not `@@`
description: NonceTagHelper claims every `<script>`; the tag-helper attribute parser mangles `@@` (literally splices buffered attribute text in at the escape site). Use `&#64;` for npm scopes like `@turf`, `@mapbox`, `@microsoft`.
---

`NonceTagHelper` (in `src/Humans.Web/TagHelpers/NonceTagHelper.cs`) targets every `<script>` element to inject the CSP nonce. That makes script tags **tag-helper-bound**, and the Razor tag-helper attribute parser does NOT correctly handle `@@` inside their attribute values: it mangles the escape and splices in the literal text of a buffered attribute (e.g. `aria-label="..."`) from elsewhere on the page, producing garbage like:

```
<script src=" aria-label="https://unpkg.com/@ aria-label="turf/turf@7.1.0/turf.min.js" nonce="..."></script>
```

**Rule:** for `@`-prefixed npm scopes in a `<script src="...">` URL, use the HTML entity `&#64;` instead of the Razor escape `@@`. Browsers decode `&#64;` to `@` when fetching, so the URL works correctly.

```html
<!-- WRONG — Razor mangles @@ inside tag-helper-bound attributes -->
<script src="https://unpkg.com/@@turf/turf@7.1.0/turf.min.js"></script>

<!-- CORRECT — HTML entity bypasses Razor's parser entirely -->
<script src="https://unpkg.com/&#64;turf/turf@7.1.0/turf.min.js"></script>
```

This applies anywhere a `<script src>` references a scoped npm package: `@turf`, `@mapbox`, `@microsoft`, `@googlemaps`, etc. `<link>` tags are unaffected (no tag helper claims them).
