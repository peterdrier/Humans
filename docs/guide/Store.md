<!-- freshness:triggers
  src/Humans.Application/Services/Store/**
  src/Humans.Application/Interfaces/Store/**
  src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs
  src/Humans.Domain/Entities/StoreOrder.cs
  src/Humans.Domain/Entities/StoreOrderLine.cs
  src/Humans.Domain/Entities/StoreProduct.cs
  src/Humans.Domain/Entities/StorePayment.cs
  src/Humans.Domain/Entities/StoreInvoice.cs
  src/Humans.Infrastructure/Data/Configurations/Store/**
  src/Humans.Infrastructure/Repositories/Store/**
  src/Humans.Web/Controllers/StoreController.cs
  src/Humans.Web/Controllers/StoreAdminController.cs
  src/Humans.Web/Authorization/Requirements/StoreOrderAuthorizationHandler.cs
-->
<!-- freshness:flag-on-change
  Store catalog editing, order lifecycle, ordering deadline gate, invoice issuance, treasury sync matching, Stripe checkout, and resource-based authorization — review when Store services/entities/controllers/auth handlers change.
-->

# Store

## What this section is for

The Store is where camp leads order supplies and services for their camp — things like water, ice, and tokens — from a catalogue set up for each event year. Each order belongs to a specific camp's season and can be paid by card or by bank transfer.

Store Admin and Finance Admin look after the catalogue, keep an eye on orders, and handle the money side.

## Key pages at a glance

- **Camp orders** (`/Store`) — browse this year's catalogue and manage your camp's orders
- **Order detail** (`/Store/Order/{id}`) — an order's items, balance, and payment status; pay by card from here
- **Catalogue** (`/Store/Admin/Catalog`) — create and manage products (Store Admin)
- **Add / edit a product** (`/Store/Admin/Catalog/Edit`) — product form: name, description, price, VAT, optional deposit, ordering deadline (Store Admin)
- **Summary report** (`/Store/Admin/Summary`) — totals by camp and by product for a year (Store Admin, Finance Admin, Admin)
- **Stripe payments** (`/Store/Admin/Payments`) — reconcile card payments against the ledger and record any that weren't picked up automatically (Store Admin, Finance Admin, Admin)

## Ordering for your camp (camp leads)

### Browse and start an order

Go to `/Store` to see what's available for your camp this year. Prices are shown VAT-inclusive as the headline amount, with the net price and VAT rate underneath. Start a new order and add items for what you need. While your order is still open it tracks the current catalogue price, so if a price changes the order updates to match; once an invoice is issued the prices are frozen as shown at that point. (Your order page lists any catalogue price changes that have happened since you started it.)

You can have more than one order for the same camp season.

### Add and remove items

You can add or remove items while your order is still **open** and the product's **ordering deadline** hasn't passed. Deadlines are set per product, so check each one — once a product's deadline is past you can't add more of it, even if your order is otherwise still open.

### Billing details

While your order is open, you can fill in the billing details (name, VAT ID, address, country, email) used if an invoice is issued.

### Pay

From your order's detail page, use **Pay** to pay by card — the payment is recorded automatically once it goes through. If a payment is already awaiting clearance (e.g. a bank debit mandate that hasn't settled yet), the Pay button is hidden until that payment either confirms or fails; this prevents a double charge. Bank transfers are recorded by hand against the order by a treasurer; automatic matching from the org's accounts isn't switched on yet.

![TODO: screenshot — order detail page showing items, balance, and the Pay button]

## As a Board member / Admin (Store Admin)

The tasks below need the **Store Admin**, **Finance Admin**, or **Admin** role. Within the Store, a Store Admin can do everything a Finance Admin can.

### Manage the catalogue

Go to `/Store/Admin/Catalog` to see all products, and add or edit one at `/Store/Admin/Catalog/Edit`. Each product has a name, description, unit price (EUR), VAT rate, an optional per-unit deposit, an ordering deadline, and an active/inactive switch. The product form shows both an ex-VAT and an incl-VAT price field — enter either and the other updates automatically. Switching a product off hides it from new orders without touching orders that already include it. Products belong to a year — the current event year decides which catalogue is live.

### Summary report

`/Store/Admin/Summary` totals up orders by camp and by product for a chosen year, including a camps-by-products grid.

### Stripe payments

`/Store/Admin/Payments` reconciles card payments taken through Stripe against what's recorded on orders. Card payments normally record themselves automatically, but if that automatic step isn't set up (or a payment slips through), this page lists every Stripe checkout and flags any paid one that hasn't been recorded yet. One click records all the missing ones — pulling the amount straight from Stripe, never re-typing it — and it's safe to run again any time. Payments recorded here but no longer found in Stripe are listed separately for you to look into; nothing is ever removed automatically. Sessions that are recorded but still awaiting settlement (e.g. a SEPA debit mandate that has been captured but hasn't cleared yet) are shown as **Recorded / Pending** — they are not counted toward the order balance until Stripe confirms the transfer.

> **Heads up:** the Finance order-review screen (entering manual payments by hand and issuing invoices) isn't switched on yet.

## Related sections

- [Camps](Camps.md) — orders belong to a camp's season; camp-lead authority comes from Camps.
- [Budget](Budget.md) — the Store is part of the money side of the org.
