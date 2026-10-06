---
name: Commit file metadata before cleaning up superseded files
description: When changing file uploads or removals, save new bytes before publishing metadata and clean old files only after commit.
---

Save new bytes before publishing their storage reference or content type. Keep existing files until replacement/removal metadata and its required audit have committed.

**Why:** A failed upload must not leave metadata pointing at a missing file or destroy the previous image. Database and filesystem writes cannot share one rollback.

**How to apply:**

- Group related metadata changes in the owning repository's save.
- Preserve previous metadata when a replacement upload fails.
- After commit, clean superseded files with `CancellationToken.None`; log cleanup failures without reversing the successful operation.
- Retain the section's GDPR read gate even when an orphan file remains.
