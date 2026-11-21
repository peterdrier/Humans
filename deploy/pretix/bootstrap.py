# One-time setup of a pretix event for the Humans pretix checkout option. Idempotent: rerun it safely.
#
#   docker exec -i -e HUMANS_URL=https://humans.nobodies.team -e EVENT_SLUG=nowhere2027 \
#       -e EVENT_NAME="Nowhere 2027" -e EVENT_DATE=2027-06-08 -e TICKET_VAT=21 \
#       <pretix container> bash -c 'cd /pretix/src && python3 manage.py shell' < bootstrap.py
#
# Prints the API token and webhook secret that go into Humans' Pretix settings. Stripe keys, payment-method fees and the
# admin user's password are set by a person in pretix's control panel afterwards.
import os
import secrets
from datetime import datetime
from decimal import Decimal
from zoneinfo import ZoneInfo

from django_scopes import scopes_disabled
from pretix.api.models import WebHook
from pretix.base.models import Event, Item, Organizer, TaxRule, Team

humans = os.environ["HUMANS_URL"].rstrip("/")
slug = os.environ["EVENT_SLUG"]
org_slug = os.environ.get("ORGANIZER_SLUG", "nobodies")
webhook_secret = os.environ.get("WEBHOOK_SECRET") or secrets.token_urlsafe(32)

with scopes_disabled():
    org, _ = Organizer.objects.get_or_create(slug=org_slug, defaults={"name": "Nobodies Collective"})
    event, _ = Event.objects.get_or_create(organizer=org, slug=slug, defaults={
        "name": os.environ.get("EVENT_NAME", slug),
        "currency": "EUR",
        "date_from": datetime.fromisoformat(os.environ["EVENT_DATE"]).replace(tzinfo=ZoneInfo("Europe/Madrid")),
    })
    event.set_active_plugins(
        ["pretix.plugins.stripe", "pretix.plugins.returnurl", "pretix.plugins.manualpayment"], allow_restricted=True)
    event.save()

    s = event.settings
    s.set("timezone", "Europe/Madrid")
    s.set("locales", ["en", "es", "de", "fr", "it", "ca", "nl"])
    s.set("returnurl_prefix", humans + "/")
    # Humans owns the ticket, its QR code and the mail; pretix only takes the money.
    s.set("ticket_download", False)
    # pretix has no on/off switch for buyer order mail; an empty sales-channel list is the off switch.
    s.set("mail_sales_channel_placed_paid", [])
    s.set("mail_sales_channel_download_reminder", [])
    s.set("mail_days_order_expire_warning", 0)
    s.set("invoice_generate", "False")

    vat, _ = TaxRule.objects.get_or_create(
        event=event, name=f"IVA {os.environ.get('TICKET_VAT', '21')}%",
        defaults={"rate": Decimal(os.environ.get("TICKET_VAT", "21")), "price_includes_tax": True})
    # Humans finds these two by internal name, per event.
    ticket, _ = Item.objects.get_or_create(event=event, internal_name="humans-ticket", defaults={
        "name": "Ticket", "default_price": Decimal("0"), "admission": True, "free_price": True, "tax_rule": vat})
    donation, _ = Item.objects.get_or_create(event=event, internal_name="humans-donation", defaults={
        "name": "Donation", "default_price": Decimal("0"), "admission": False, "free_price": True})

    team, _ = Team.objects.get_or_create(organizer=org, name="Humans API", defaults={"all_events": True})
    for flag in ("all_event_permissions", "can_view_orders", "can_change_orders"):
        if hasattr(team, flag):
            setattr(team, flag, True)
    team.save()
    token = team.tokens.filter(active=True).first() or team.tokens.create(name="humans")

    target = f"{humans}/Pretix/Webhook/{webhook_secret}"
    hook = WebHook.objects.filter(organizer=org, target_url__startswith=f"{humans}/Pretix/Webhook/").first()
    if hook is None:
        hook = WebHook.objects.create(organizer=org, target_url=target, enabled=True, all_events=True)
        for action in ("placed", "paid", "canceled", "expired", "modified", "payment.confirmed",
                       "refund.created", "refund.done", "refund.failed", "refund.canceled"):
            hook.listeners.create(action_type=f"pretix.event.order.{action}")
    else:
        webhook_secret = hook.target_url.rsplit("/", 1)[-1]

    print(f"Pretix__BaseUrl        = <this pretix's URL>")
    print(f"Pretix__Organizer      = {org.slug}")
    print(f"PRETIX_API_TOKEN       = {token.token}")
    print(f"PRETIX_WEBHOOK_SECRET  = {webhook_secret}")
    print(f"Tickets sales event: backend 'pretix', event id in the backend '{event.slug}', ticket VAT {vat.rate / 100}")
