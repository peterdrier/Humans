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
   and Klarna, and set each method's fee if the price may differ by method. Put the event live.
6. In Humans, set `Pretix:BaseUrl`, `Pretix:Organizer` and the env vars `PRETIX_API_TOKEN`,
   `PRETIX_WEBHOOK_SECRET` from the bootstrap output (once per instance), then at
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
- Upgrades: bump the image tag in both services; migrations run on start.
