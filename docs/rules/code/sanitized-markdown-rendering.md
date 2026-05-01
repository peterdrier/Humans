---
name: Markdown rendering uses the shared sanitized path
description: Use `@Html.SanitizedMarkdown(...)` in Razor — never inline `HtmlSanitizer`, `Markdig.Markdown.ToHtml`, or local `ConvertMarkdownToHtml(...)` helpers.
---

Markdown rendering in Razor must go through the shared sanitized rendering path.

**Rule:**
- Do not embed local `HtmlSanitizer`, `Markdig.Markdown.ToHtml`, or `ConvertMarkdownToHtml(...)` helpers in views
- Use `@Html.SanitizedMarkdown(...)` for one-off markdown rendering
- If multiple pages share tabbed markdown document UI, extract or reuse a shared partial/component rather than duplicating tabs + sanitizer logic

**Why:** Prevents inconsistent sanitization and removes duplicated Markdown boilerplate from views.
