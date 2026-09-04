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
callback really came from Stripe and says which checkout event it is. Separately, the
finance side wants to know what a ticket purchase actually cost after Stripe's cut, so this
section looks a payment up and reports the fees. It decides nothing about what a payment
*means* — it translates, and hands the translation to whoever asked.

Side jobs run at boot. One checks each configured key still works, so a wrong or
under-permissioned key shows up in the log at startup instead of at the first real payment.
The other exists only for throwaway preview environments: it tells Stripe where to deliver
callbacks for this preview, and tidies up the delivery addresses of previews that are gone.

## 2. The shapes

Everything the section exposes, grouped by the question it answers.

| Shape | Members | What the caller is asking |
|---|---|---|
| **Can I?** | `IsConfigured`, `IsStoreCheckoutConfigured`, `IsStoreWebhookConfigured` | "Is this capability wired up?" — asked *before* the matching call, so the caller can hide a button or skip a pass rather than handle a failure. |
| **Take a payment** | `CreateCheckoutSessionAsync` | "Give me a URL that charges this order this amount." The only shape that writes to Stripe. |
| **What did Stripe say?** | `ParseStoreCheckoutEvent` | "Is this callback genuine, and which checkout event is it?" Verification and categorisation, no decision. |
| **What does Stripe hold?** | `GetPaymentDetailsAsync`, `ListStoreCheckoutSessionsAsync` | "Read the account back to me." Fee breakdown for one payment; every session for reconciliation. |
| **Boot-time self-check** | `StripeStartupSmokeService` | Not a caller shape — a job. One low-risk read per configured key. |
| **Ephemeral-env plumbing** | `StoreWebhookRegistrationService` | Not a caller shape — a job. Point Stripe at this host; unpoint it from dead hosts. |

What the rest of this file leans on:

- A **read** shape returns `null` for the cases the section *checks* — a key that is unset, a
  key that lacks the scope, a signature that does not verify, a PaymentIntent Stripe returned
  with no charge on it — and an empty collection means "Stripe answered, with nothing". Every
  other failure propagates: `null` says the section has nothing to hand back, never that the
  call failed. That distinction is the section's one behavioural rule, and it must hold
  uniformly across the read shapes — one that returns `null` where its siblings throw would
  tell a caller a transport failure was a missing key.
- Every shape that needs a key is preceded by the matching **Can I?** flag, and each
  implementation re-checks it rather than trusting the caller.
- The jobs share nothing with the caller shapes but the settings object.

## 3. Structure

```
Contracts/IStripeService.cs   the seam: the interface, its capability flags, its methods, its DTOs
Services/StripeService.cs     the seam's only implementation + StripeSettings
Services/StripeStartupSmokeService.cs    boot job: probe each key
Services/StoreWebhookRegistrationService.cs  boot job: ephemeral-env webhook lifecycle
Section.cs                    env-var binding + the section's DI registrations
Docs/                         Stripe.md (invariants), data-access.md, health.md (this)
```

Written fresh, this is what the section would be. One note on what stays as it is:

- `StripeSettings` lives in `StripeService.cs` above the service. Every service in the
  section reads it, and only one of them is in that file. It belongs in its own file next to them — but it is
  `internal`, one screen long, and moving it buys a reader nothing they do not already get
  from one grep. **Not worth a move; noted so the next run stops re-asking.**

The registrar's endpoint cleanup is **a single listing**, deciding per endpoint against both
predicates it has: is this our own URL, and is this a closed PR's. The closed-PR deletions run
first and the own-URL deletion last, so a failure part-way through leaves the account as the
boot found it rather than with this host's endpoint gone and not yet recreated.

## 4. Invariants

- No Stripe.NET SDK type appears anywhere on `Contracts/` — at any generic depth.
- `Humans.Stripe` is the only production project referencing `Stripe.net`.
- Everything outside `Contracts/` and `Section` is `internal sealed`.
- The section references no other section. Consumers reference *it* — Store and Tickets both
  do — and that one-way edge is why it needs no `.Contracts` leaf: there is no cycle to break.
- The section owns no table, no `DbContext`, no repository, no controller, no view, no
  resource file, and no route.
- **Reads return `null` when the section has nothing to hand back** — the configuration says
  Stripe cannot be asked (key unset, scope missing, signature invalid), or, on
  `GetPaymentDetailsAsync` alone, Stripe answered with a PaymentIntent carrying no charge. A
  `null` is never confused with "Stripe said nothing": an empty collection is that.
- **A read does not swallow a failure.** An authentication error, a rate limit, a transport
  failure or cancellation propagates out of the network reads; only the cases the section
  checks become `null`. (`ParseStoreCheckoutEvent` is the exception that proves it: it does no
  network I/O, so every `StripeException` it can see *is* a bad payload.)
- **The write throws.** `CreateCheckoutSessionAsync` rejects a non-positive amount and an
  unset Store key before any network call, and lets a Stripe failure propagate.
- EUR → minor units rounds half away from zero; minor units → EUR is exact (a `long` over
  `100m` cannot round). `ToStripeMinorUnits` and `FromStripeMinorUnits` are the only places
  either conversion happens, and the round trip is lossless.
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
- **No retry, backoff or circuit breaker.** One server, low volume. A Stripe read that fails
  for a transport or rate-limit reason throws, and the next sync pass or page load asks
  again; nothing here treats a failure as a value.
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
- **The hosted services start unordered — no `IHostedService` guarantee between them.** The smoke
  probe may log "webhook secret not set" moments before the registrar stamps one. Cosmetic,
  and cheaper than coordinating them.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-04 | First doctor pass — one real bug (the only read that threw where its siblings return `null`, unreachable today because its one caller guards), the registrar's duplicate endpoint listing collapsed, and the prose trimmed of a shipped-work TODO, a type that does not exist and the G5 migration's provenance | peterdrier/Humans#1588 |
