# Stripe — Data Access

## Stripe

Project: `src/Sections/Humans.Stripe` — services under `Services/`. **No
DbContext, no repository, no tables:** the section is the Stripe payment
connector; every persisted artefact of a payment belongs to Store.

### StripeService (Scoped)

No repository. Wraps the Stripe SDK using the keys in
`IOptions<StripeSettings>`: `CreateCheckoutSessionAsync`,
`GetPaymentDetailsAsync`, `ListStoreCheckoutSessionsAsync`, and
`ParseStoreCheckoutEvent`, which verifies the webhook signature against
`StoreWebhookSecret`. Holds no DB access and no cache; callers persist results
through their own sections.

---
