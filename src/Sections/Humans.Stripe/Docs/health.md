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
callback really came from Stripe and says which checkout event it is. Finance wants to know
what a ticket purchase cost after Stripe's cut, so the section looks a payment up and reports
every fee bucket. It decides nothing about what a payment *means* — it translates, and hands
the translation to whoever asked.

Two side jobs run at boot: one checks each configured key still works, so a wrong or
under-permissioned key shows up in the log at startup; the other, in throwaway preview
environments only, tells Stripe where to deliver this preview's callbacks and removes the
delivery addresses of previews that are gone.

## 2. The shapes

| Shape | Members | What the caller is asking |
|---|---|---|
| **Can I?** | `IsConfigured`, `IsStoreCheckoutConfigured`, `IsStoreWebhookConfigured` | "Is this capability wired up?" — asked before the matching call, so the caller can hide a button or skip a pass. |
| **Take a payment** | `CreateCheckoutSessionAsync` | "Give me a URL that charges this order this amount." The only shape that writes to Stripe. |
| **What did Stripe say?** | `ParseStoreCheckoutEvent` | "Is this callback genuine, and which checkout event is it?" Verification and categorisation, no decision. |
| **What does Stripe hold?** | `GetPaymentDetailsAsync`, `ListStoreCheckoutSessionsAsync` | "Read the account back to me." Fee breakdown for one payment; every session for reconciliation. |
| **Boot-time self-check** | `StripeStartupSmokeService` | Not a caller shape — a job. One low-risk read per configured key. |
| **Ephemeral-env plumbing** | `StoreWebhookRegistrationService` | Not a caller shape — a job. Point Stripe at this host; unpoint it from dead hosts. |

The read shapes share one rule (§4): `null` says the section has nothing to hand back, never
that the call failed. One read that returned `null` where its siblings throw would tell a
caller a transport failure was a missing key.

## 3. Structure

```
Contracts/IStripeService.cs   the seam: the interface, its capability flags, its methods, its DTOs
Services/StripeService.cs     the seam's only implementation + StripeSettings
Services/StripeStartupSmokeService.cs    boot job: probe each key through one probe pipeline
Services/StoreWebhookRegistrationService.cs  boot job: ephemeral-env webhook lifecycle
Section.cs                    env-var binding + the section's DI registrations
SectionConfiguration.cs       declares the same STRIPE_* and Stripe:* keys to the settings inventory
Docs/                         Stripe.md (invariants), data-access.md, health.md (this)
```

- `StripeSettings` stays in `StripeService.cs`: internal and one screen long, so a move buys
  a reader nothing.
- The registrar's endpoint cleanup is a single listing, deciding per endpoint whether it is
  this host's own URL or a closed PR's. Closed-PR deletions run first and the own-URL deletion
  last, so a failure part-way leaves the account as boot found it.

## 4. Invariants

- No Stripe.NET SDK type appears on `Contracts/`, at any generic depth —
  `tests/Humans.Stripe.Tests/Architecture/StripeConnectorArchitectureTests.cs:15`.
- The section references no other section: `src/Sections/Humans.Stripe/Humans.Stripe.csproj:14`
  is its only `ProjectReference`, to `Humans.Base`.
- **Reads return `null` only for the cases the section checks:** key unset
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:135`,
  `src/Sections/Humans.Stripe/Services/StripeService.cs:277`), scope missing
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:152`,
  `src/Sections/Humans.Stripe/Services/StripeService.cs:294`), signing secret unset or
  signature invalid (`src/Sections/Humans.Stripe/Services/StripeService.cs:233`,
  `src/Sections/Humans.Stripe/Services/StripeService.cs:248`), a webhook session without EUR
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:267`), and — `GetPaymentDetailsAsync`
  alone — a PaymentIntent with no charge or no balance transaction yet
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:161`,
  `src/Sections/Humans.Stripe/Services/StripeService.cs:170`). Every other `StripeException`
  propagates: those catches are filtered on `permission_error`, and the propagation is pinned
  by `StripeServiceContractTests.Reads_and_the_write_propagate_every_other_stripe_failure`.
- **The write throws.** `CreateCheckoutSessionAsync` rejects a non-positive amount and an
  unset Store key before any network call
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:72`,
  `src/Sections/Humans.Stripe/Services/StripeService.cs:74`) and rethrows a permission error
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:127`).
- **Amounts are EUR or nothing.** A balance transaction or fee entry without EUR throws
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:175`,
  `src/Sections/Humans.Stripe/Services/StripeService.cs:192`); a session without EUR is not
  projected (`src/Sections/Humans.Stripe/Services/StripeService.cs:308`) and rejects the whole
  reconciliation list (`src/Sections/Humans.Stripe/Services/StripeService.cs:290`).
- Fee buckets that do not sum to the balance transaction's total log a Warning
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:216`).
- EUR → minor units rounds half away from zero; minor units → EUR is exact
  (`src/Sections/Humans.Stripe/Services/StripeService.cs:57`,
  `src/Sections/Humans.Stripe/Services/StripeService.cs:60`).
- Neither boot job can block startup: both start their work on `Task.Run` and return
  (`src/Sections/Humans.Stripe/Services/StripeStartupSmokeService.cs:24`,
  `src/Sections/Humans.Stripe/Services/StoreWebhookRegistrationService.cs:40`), and each ends
  its catch ladder on `Exception`
  (`src/Sections/Humans.Stripe/Services/StripeStartupSmokeService.cs:83`,
  `src/Sections/Humans.Stripe/Services/StoreWebhookRegistrationService.cs:125`).
- The registrar cannot act without `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY`
  (`src/Sections/Humans.Stripe/Services/StoreWebhookRegistrationService.cs:49`), and cannot
  touch an endpoint whose host is not `*.n.burn.camp` with path `/Store/StripeWebhook`
  (`src/Sections/Humans.Stripe/Services/StoreWebhookRegistrationService.cs:148`).

## 5. Seams

- **Per-account key rotation.** The settings shape assumes one key per account for the life of
  the process; the registrar already mutates one of them at boot. Nothing asks for rotation yet.

## 6. Deliberately not done

- **No `I<Section>Write`.** This section writes no table — its "write" is an HTTP call.
- **No retry, backoff or circuit breaker.** One server, low volume: a failing read throws and
  the next sync pass or page load asks again.
- **No caching decorator.** Every call exists to see Stripe's *current* state; a cached
  reconciliation list would be worse than none.
- **No payouts or chargebacks.** Dashboard-manual by standing decision
  (`memory/architecture/stripe-money-out.md`). Refunds are in scope through one guarded path,
  not built yet (nobodies-collective/Humans#1254).
- **No `.Contracts` leaf project.** There is no cycle for it to break.
- **No scope introspection.** Stripe exposes no endpoint that reports a restricted key's
  scopes; the smoke probe can only confirm the scopes used are present.

## Load-bearing weirdness

- **`using Stripe;` stays at compilation-unit level, and no SDK type is qualified inline.**
  Inside `Humans.Stripe.*` the bare name `Stripe` binds to the section. Simple names or
  `global::Stripe.X`, never `Stripe.X`.
- **The registrar mutates `IOptions<StripeSettings>.Value` in place** to publish the signing
  secret Stripe just minted. It works because the scoped service holds the same cached
  instance; it is the only writable settings field, and the reason `StripeService` may cache
  `settings.Value` in a field.
- **The registrar logs its success path at Warning.** It is the only on-host confirmation
  that the secret was stamped, and deployed environments filter Information.
- **`Humans.Stripe.Tests` is the one test project allowed to reference `Stripe.net`** — the
  signature tests hand-sign a payload and feed it to the real `EventUtility`.
- **The hosted services start unordered.** The smoke probe may log "webhook secret not set"
  moments before the registrar stamps one. Cosmetic, and cheaper than coordinating them.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-04 | First doctor pass — one real bug (the only read that threw where its siblings return `null`, unreachable today because its one caller guards), the registrar's duplicate endpoint listing collapsed, and the prose trimmed of a shipped-work TODO, a type that does not exist and the G5 migration's provenance | peterdrier/Humans#1588 |
| 2 | 2026-10-26 | The null-versus-propagate rule pinned on every network call, the two smoke probes folded into one pipeline, the registrar on the SDK's event names, and Stripe.md's false rounding claim corrected | peterdrier/Humans#2160 |
