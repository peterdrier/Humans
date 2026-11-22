# pretix on Coolify

Hosts pretix for the Humans pretix checkout option (`src/Sections/Humans.Pretix`). Stock
`pretix/standalone`, no fork patches: Humans creates orders through the API at its own prices and
pretix only takes the payment.

## Deploy

1. Coolify: New resource, Docker Compose, point it at this folder's `docker-compose.yml`.
   Give the `pretix` service a domain (e.g. `https://pretix.nobodies.team`). Coolify generates
   `SERVICE_FQDN_PRETIX` and `SERVICE_PASSWORD_POSTGRES`; set `PRETIX_MAIL_*` if pretix should
   send its own mails (Humans sends the ticket mail, so this is only for admin mail).
2. Deploy. First boot migrates the database, which takes a few minutes.
3. Create the admin: `docker exec -it <pretix container> pretix createsuperuser`
   (or log in as `admin@localhost` / `admin` and change it immediately).
4. Set up the event and print Humans' settings:

   ```bash
   docker exec -i -e HUMANS_URL=https://humans.nobodies.team -e EVENT_SLUG=nowhere2027 \
     -e EVENT_NAME="Nowhere 2027" -e EVENT_DATE=2027-06-08 -e TICKET_VAT=21 \
     <pretix container> bash -c 'cd /pretix/src && python3 manage.py shell' < bootstrap.py
   ```

5. In pretix's control panel, per event: connect Stripe (Payment, Stripe), enable card, iDEAL
   and Klarna, and leave every method's fee at zero (Humans charges exactly the lines). With pretix's own
   Stripe keys (not Stripe Connect), register the webhook pretix shows there (`https://<pretix>/_stripe/webhook/`)
   in Stripe: it is the only way pretix, and so Humans, hears of a refund made in the Stripe dashboard or a
   chargeback. Put the event live.
6. In Humans, set `Pretix:BaseUrl`, `Pretix:Organizer` and the env vars `PRETIX_API_TOKEN`,
   `PRETIX_WEBHOOK_SECRET` from the bootstrap output (once per instance), and `STRIPE_PRETIX_KEY`, a
   read-only restricted key on the same Stripe account (Balance transactions, Payouts, PaymentIntents,
   Charges, Refunds and Disputes read) so Humans reconciles its fees and payouts, then at
   `/Tickets/Admin/Sales` pick backend `pretix` and the event slug. Each further event only needs
   the bootstrap run with its own `EVENT_SLUG`; Humans finds its items by internal name
   (`humans-ticket`, `humans-donation`).

## Notes

- pretix refuses webhooks to private addresses. Point the webhook at Humans' public URL, not a
  Docker-internal one (or set `PRETIX_PRETIX_ALLOW_HTTP_TO_PRIVATE_NETWORKS=on`, which also opens
  every other outbound call to the private network).
- The API token can create orders and refund them; pretix has one permission for both. It sits
  only in Humans' environment.
- `pretix-cron` runs pretix's periodic tasks every 5 minutes (order expiry, webhook retries).
- Chargebacks show in pretix as an external refund waiting to be processed. Leave it unprocessed until the
  dispute is final: cancel it if the event won (that clears the flag in Humans); if lost, process it choosing to
  cancel the order (mark refunded). Processing without cancel reopens the order and its pretix page asks the buyer
  to pay the full amount again. A processed one can no longer be cancelled, so a dispute won after that stays
  flagged in Humans.
- Humans counts a Stripe dashboard refund while it is still unprocessed, so leave it unprocessed in pretix:
  processing a partial one reopens the order owing the refunded amount. Refund through Humans instead of the
  Stripe dashboard where possible.
- Upgrades: bump the image tag in both services; migrations run on start.
