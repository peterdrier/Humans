---
name: bin/ keeps only the host RID's native assets
description: Builds prune Magick.NET native assets for other RIDs from bin/; pass `-p:KeepAllRuntimeAssets=true` to keep them all.
---

`Directory.Build.props` targets `PruneForeignMagickRuntimeAssets` and `CleanForeignMagickRuntimeDirs` drop Magick.NET's native assets (`runtimes/<rid>/native`) whose RID isn't the SDK's host RID, when no `RuntimeIdentifier` is set, and delete the foreign `runtimes/<rid>` dirs left in bin by earlier builds (the first build reclaims the space). Other packages' natives, including generic-RID ones like `runtimes/unix/native`, are untouched.

**Why:** Magick.NET ships natives for 8 platforms, ~236 MB per executable output. 10 outputs carry them (Web plus 9 test projects), ~2 GB per worktree.

**How to apply:** Nothing, by default. For a bin built on one OS/arch and run on another (cross-build, Windows bin under WSL), build with `-p:KeepAllRuntimeAssets=true`. Linux CI and Docker are unaffected: no RID is set and Linux hosts keep `linux-x64`.
