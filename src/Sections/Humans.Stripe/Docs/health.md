<!-- freshness:triggers
  src/Sections/Humans.Stripe/**
  tests/Humans.Stripe.Tests/**
-->

# Stripe — Target Shape

The shape this section is converging on, regenerated every section-doctor run and diffed
against the previous run's copy. Not a history: what the section *should* be today.

## 1. What the section does

It is the one place the organisation talks to Stripe. Someone buying from the Store gets a
Stripe-hosted payment page; when they pay, Stripe calls back and this section proves the
callback really came from Stripe and says which of four things happened. Separately, the
finance side wants to know what a ticket purchase actually cost after Stripe's cut, so this
section looks a payment up and reports the fees. It decides nothing about what a payment
*means* — it translates, and hands the translation to whoever asked.

Two side jobs run at boot. One checks each configured key still works, so a wrong or
under-permissioned key shows up in the log at startup instead of at the first real payment.
The other exists only for throwaway preview environments: it tells Stripe where to deliver
callbacks for this preview, and tidies up the delivery addresses of previews that are gone.

## 2. The shapes

Everything the section exposes, grouped by the question it answers.

| Shape | Members | What the caller is asking |
|---|---|---|
| **Can I?** | `IsConfigured`, `IsStoreCheckoutConfigured`, `IsStoreWebhookConfigured` | "Is this capability wired up?" — asked *before* the matching call, so the caller can hide a button or skip a pass rather than handle a failure. |
| **Take a payment** | `CreateCheckoutSessionAsync` | "Give me a URL that charges this order this amount." The only shape that writes to Stripe. |
| **What did Stripe say?** | `ParseStoreCheckoutEvent` | "Is this callback genuine, and which of the four events is it?" Verification and categorisation, no decision. |
| **What does Stripe hold?** | `GetPaymentDetailsAsync`, `ListStoreCheckoutSessionsAsync` | "Read the account back to me." Fee breakdown for one payment; every session for reconciliation. |
| **Boot-time self-check** | `StripeStartupSmokeService` | Not a caller shape — a job. One low-risk read per configured key. |
| **Ephemeral-env plumbing** | `StoreWebhookRegistrationService` | Not a caller shape — a job. Point Stripe at this host; unpoint it from dead hosts. |

Three consequences the rest of this file leans on:

- Every **read** shape returns `null` for "could not ask Stripe", never an empty result and
  never an exception. The **write** shape throws. That split is the section's one behavioural
  rule and it must hold *uniformly* — a read that throws where its siblings return `null` is a
  defect, not a variation.
- Every shape that needs a key is preceded by the matching **Can I?** flag, and each
  implementation re-checks it rather than trusting the caller.
- The two jobs share nothing with the caller shapes but the settings object.

## 3. Structure

```
Contracts/IStripeService.cs   the seam: one interface, three flags, four methods, three DTOs
Services/StripeService.cs     the seam's only implementation + StripeSettings
Services/StripeStartupSmokeService.cs    boot job: probe each key
Services/StoreWebhookRegistrationService.cs  boot job: ephemeral-env webhook lifecycle
Section.cs                    env-var binding + three DI registrations
Docs/                         Stripe.md (invariants), data-access.md, health.md (this)
```

Written fresh, this is what the section would be. One note on what stays as it is:

- `StripeSettings` lives in `StripeService.cs` above the service. It is read by three types,
  only one of which is in that file. It belongs in its own file next to them — but it is
  `internal`, one screen long, and moving it buys a reader nothing they do not already get
  from one grep. **Not worth a move; noted so the next run stops re-asking.**

The registrar's endpoint cleanup reached this shape on 2026-09-04: **one listing, one deletion
pass, two predicates** (our own URL, and a closed PR's). It was two identical
`WebhookEndpointService.ListAsync` calls through two methods deleting from the same list.

## 4. Invariants

- No Stripe.NET SDK type appears anywhere on `Contracts/` — at any generic depth.
- `Humans.Stripe` is the only production project referencing `Stripe.net`.
- Everything outside `Contracts/` and `Section` is `internal sealed`.
- The section references no other section, in either direction of the project graph. That
  acyclicity is why it needs no `.Contracts` leaf.
- The section owns no table, no `DbContext`, no repository, no controller, no view, no
  resource file, and no route.
- **Reads return `null` when Stripe cannot be asked** — key unset, scope missing, signature
  invalid — and never confuse that with "Stripe said nothing". Applies to
  `GetPaymentDetailsAsync`, `ListStoreCheckoutSessionsAsync` and `ParseStoreCheckoutEvent`
  alike.
- **The write throws.** `CreateCheckoutSessionAsync` rejects a non-positive amount and an
  unset Store key before any network call, and lets a Stripe failure propagate.
- EUR → minor units rounds half away from zero; minor units → EUR is exact (a `long` over
  `100m` cannot round). Both directions live in exactly one pair of functions, and the
  round trip is lossless.
- Neither boot job can block, delay or fail startup, and neither throws out of its own body.
- The registrar cannot act without `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY`, and cannot touch an
  endpoint whose host is not `*.n.burn.camp` with path `/Store/StripeWebhook`.

## 5. Seams

- **Per-account key rotation.** The settings shape assumes one key per account for the life of
  the process; the registrar already mutates one of them at boot, which is the first crack in
  that assumption. Nothing asks for rotation yet. Reserved, not built.

## 6. Deliberately not done

- **No read/write interface split.** `I<Section>ServiceRead` separates table readers from
  table writers; this section writes no table — its "write" is an HTTP call — so the split has
  nothing to separate.
- **No retry, backoff or circuit breaker.** One server, low volume; a failed Stripe read is
  surfaced as `null` and retried by the next sync pass or the next page load.
- **No caching decorator.** Every call exists to see Stripe's *current* state; a cached
  reconciliation list would be worse than no reconciliation list.
- **No refunds, payouts or chargebacks.** Dashboard-manual by standing decision
  (`memory/architecture/refunds-manual-via-dashboard.md`).
- **No `.Contracts` leaf project.** Nothing would break the cycle it exists to break.
- **No scope introspection.** Stripe exposes no endpoint that reports a restricted key's
  scopes; the smoke probe can only confirm the scopes used are present.

## Load-bearing weirdness

- **`using Stripe;` must stay at compilation-unit level, and no SDK type may be qualified
  inline.** Inside `Humans.Stripe.*` the bare name `Stripe` binds to the section, not the
  vendor. Simple names or `global::Stripe.X`, never `Stripe.X`.
- **The registrar mutates `IOptions<StripeSettings>.Value` in place** to publish the signing
  secret Stripe just minted. It works because `IOptions<T>.Value` is one cached instance the
  scoped service also holds. It is the only writable settings field, and it is the reason
  `StripeService` may cache `settings.Value` in a field.
- **The registrar logs its success path at Warning, not Information.** Deliberate: it is the
  only on-host confirmation that the secret was stamped, and Information is filtered in
  deployed environments.
- **`Humans.Stripe.Tests` is the one test project allowed to reference `Stripe.net`** — the
  signature sanity test hand-signs a payload and feeds it to the real `EventUtility`.
- **Two hosted services, no `IHostedService` ordering guarantee between them.** The smoke
  probe may log "webhook secret not set" moments before the registrar stamps one. Cosmetic,
  and cheaper than coordinating them.
