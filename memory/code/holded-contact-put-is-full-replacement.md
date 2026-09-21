---
name: Holded contact PUT is a full replacement
description: Never update a Holded contact with a partial body — v2 `PUT /contacts/{id}` resets every omitted field, supplier_record included, minting a new creditor account. A linked contact is used as is.
---
Never send `PUT /api/v2/contacts/{id}` with a partial body. Once a member is linked to a Holded contact, use that contact id as is; only a member with no contact gets a POST.

**Why:** Holded's v2 contact update is a full replacement ("any field you omit from the request body will be reset to its default value"). An expense-report push that PUT name/trade name/IBAN reset `supplier_record`, and the next purchase doc minted the member a second creditor account (40000004 → 40000060, 2026-09-21), orphaning their history and silently rebinding them. Omitting `type` to "preserve" it had the same inverted assumption.

**How to apply:** `EnsureCreditorContactAsync` skips Holded entirely when a binding or seed carries a contact id. Name/IBAN changes after the first push do not reach Holded until the link-check sync (peterdrier/Humans#1777); that sync must read-merge-write the full contact or use a partial-update endpoint, never a partial PUT. `HoldedClient.UpsertContactAsync`'s `ExistingContactId` branch is uncalled and unsafe — do not reach for it.
