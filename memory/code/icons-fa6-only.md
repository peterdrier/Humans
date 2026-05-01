---
name: Icons — Font Awesome 6 only
description: Use `fa-solid fa-*` (or `fa-regular`, `fa-brands`). Never `bi bi-*` (Bootstrap Icons not loaded → renders invisibly).
---

This project uses **Font Awesome 6** (loaded via CDN in `_Layout.cshtml`). Bootstrap Icons are **not** loaded and will render as invisible/missing.

**Rule:** Always use `fa-solid fa-*` (or `fa-regular fa-*`, `fa-brands fa-*`) classes for icons. Never use `bi bi-*` (Bootstrap Icons).

```html
<!-- WRONG — Bootstrap Icons not loaded, will be invisible -->
<i class="bi bi-gear"></i>

<!-- CORRECT — Font Awesome 6 -->
<i class="fa-solid fa-gear"></i>
```
