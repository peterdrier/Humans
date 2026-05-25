---
name: Migration regen is unsafe after a rebase that interleaves main's migrations
description: HARD RULE. Once your branch's migrations are mid-chain (main raced ahead with later-timestamped migrations), `dotnet ef migrations remove` is broken for your migrations. STOP and ask Peter. Don't hand-edit the snapshot. Don't reset+redo without explicit permission.
---

`dotnet ef migrations remove` only walks back the **most recent** migration by timestamp, and reverts the snapshot using the **previous-by-timestamp Designer file** as the source of truth.

This works cleanly only when **your** branch's migration is the most recent. When main has raced ahead with later-timestamped migrations during your PR's life, your branch's migrations become **mid-chain**, and `migrations remove` is no longer safe for them.

**The mechanical failure:**

If your migration sequence (after rebasing main) looks like:

```
20260430014508_AddStoreSection           ← yours
20260430125045_AddStoreSectionForeignKeys ← yours, want to redo this
20260501170853_AddUserMergedToUserId     ← from main (deployed to QA)
20260502160719_AddIssues                 ← from main (deployed to QA)
20260502171652_AddAgentSection           ← from main (deployed to QA)
20260504135703_DropStoreOrderCampSeasonFK ← yours (latest)
```

Then `dotnet ef migrations remove` removes `DropStoreOrderCampSeasonFK` (the latest by timestamp). To get back to the migration you actually want to redo (`AddStoreSectionForeignKeys`), you'd have to keep removing — which would walk through and **delete main's migrations from your branch**. They've already been deployed to QA; deleting them from the branch breaks the next deploy because `__EFMigrationsHistory` will reference migrations that no longer exist in the assembly.

**Even removing just your own latest migration is broken** because the previous-by-timestamp Designer (one of main's, e.g. `AddAgentSection.Designer.cs`) was generated on a branch that didn't have your tables. Its embedded snapshot has no record of them. `migrations remove` reverts the live snapshot to that pre-your-tables state. Subsequent `migrations add` then sees "model has my tables, snapshot doesn't" and generates a giant "create everything" migration. Empirically observed: 3784-line `CreateTable` mega-migration when this happened on PR 373.

**The hard rule:** in this situation, **stop and ask Peter**. The canonical recovery — once Peter has confirmed — is the `/ef-regen` skill at `.claude/skills/ef-regen/SKILL.md`, which deletes the in-flight migrations, restores the snapshot from `origin/main`, and lets `dotnet ef migrations add` produce one consolidated migration with a fresh end-of-chain timestamp. That skill is the *only* sanctioned use of `git checkout origin/main -- HumansDbContextModelSnapshot.cs`; outside the skill's workflow that command is still forbidden.

Do not improvise outside `/ef-regen`. Specifically forbidden "creative solutions":

1. **Hand-editing the snapshot** to remove the relationships you want EF to "regen." This violates [`no-hand-edited-migrations`](no-hand-edited-migrations.md). The end state may look correct but the migration history is structurally janky and lives forever in archaeology.
2. **Renaming migration timestamps** to force your migration to end-of-chain. Same rule — that's still a hand-edit.
3. **Bulk `migrations remove` through main's migrations** to get back to yours. Breaks deploy compatibility.
4. **`git reset --hard` to undo the migration's existence and start over from end-of-chain.** Sometimes this IS the right answer, but it has cost (replaying all the work that depended on the migration filename existing) and is a stop-and-ask event, not a default.

**Why "stop and ask":** the right path depends on context Peter has and you don't:
- Has the affected migration been deployed to a long-lived env (QA/prod)? If yes, it's frozen — must add a *new* corrective migration, not regen the old one.
- Is the issue critical (HARD RULE violation in committed history) or cosmetic (minor schema reorder)? Cosmetic ones may not be worth the cleanup cost.
- Does the PR plan support the reset+rebase+regen flow? Big branches that have layered work on top of the migration filename may not.

**Prevention — regen BEFORE rebase, not after:**

If you suspect a migration on your branch needs regen (e.g., you spot a HARD RULE violation, or a config bug), do the regen **before** rebasing main's later migrations in. While your migration is still end-of-chain, `migrations remove` works cleanly:

```bash
dotnet ef migrations remove          # latest, your migration
# adjust model/config
dotnet build src/Humans.Web -v quiet # MUST rebuild between operations
dotnet ef migrations add <Name>      # regenerates with current timestamp
```

Once main's migrations interleave, the window closes. Designer snapshots from the rebased migrations are blind to your work and the tooling becomes unsafe.

**The Designer-snapshot inconsistency that lives forever:**

Even when migrations apply correctly at runtime, the Designer snapshots embedded in main's interleaved migrations DO NOT reflect your branch's tables. Reading the Designer chain in timestamp order, the model state appears to "lose your tables in the middle and re-gain them at the end." This is invisible at runtime (EF doesn't validate intermediate Designer snapshots — only the latest top-level snapshot matters for tooling), but it makes future `migrations remove` of your branch-migrations permanently unsafe. The damage compounds with every later interleaving.

This is a structural property of EF Core's design, not a Humans bug. The mitigation is the workflow rule above: regen before rebase, not after.

**Pre-commit hook coverage:** the existing pre-commit hook on `src/Humans.Infrastructure/Migrations/*.cs` catches some hand-edits but does not (and cannot easily) detect snapshot edits or timestamp renames. Self-discipline + this atom is the enforcement mechanism.
