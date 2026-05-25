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

## Ordering for your camp (camp leads)

### Browse and start an order

Go to `/Store` to see what's available for your camp this year. Start a new order and add items for what you need. Each item locks in the product's price, VAT, and deposit at the moment you add it — later catalogue changes won't shift your existing items.

You can have more than one order for the same camp season; give each a label to tell them apart.

### Add and remove items

You can add or remove items while your order is still **open** and the product's **ordering deadline** hasn't passed. Deadlines are set per product, so check each one — once a product's deadline is past you can't add more of it, even if your order is otherwise still open.

### Billing details

While your order is open, you can fill in the billing details (name, VAT ID, address, country, email) used if an invoice is issued.

### Pay

From your order's detail page, use **Pay** to pay by card — the payment is recorded automatically once it goes through. Bank transfers are matched from the org's accounts and applied automatically when the next sync runs.

![TODO: screenshot — order detail page showing items, balance, and the Pay button]

## As a Board member / Admin (Store Admin)

The tasks below need the **Store Admin**, **Finance Admin**, or **Admin** role. Within the Store, a Store Admin can do everything a Finance Admin can.

### Manage the catalogue

Go to `/Store/Admin/Catalog` to see all products, and add or edit one at `/Store/Admin/Catalog/Edit`. Each product has a name, description, unit price (EUR), VAT rate, an optional per-unit deposit, an ordering deadline, and an active/inactive switch. Switching a product off hides it from new orders without touching orders that already include it. Products belong to a year — the current event year decides which catalogue is live.

### Summary report

`/Store/Admin/Summary` totals up orders by camp and by product for a chosen year, including a camps-by-products grid.

> **Heads up:** the Finance order-review screen (entering manual payments by hand and issuing invoices) isn't switched on yet.

## Related sections

- [Camps](Camps.md) — orders belong to a camp's season; camp-lead authority comes from Camps.
- [Budget](Budget.md) — the Store is part of the money side of the org.
