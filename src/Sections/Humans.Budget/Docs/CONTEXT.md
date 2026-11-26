# Budget — Language

The yearly plan of what the association expects to earn and spend. Settled in the 2026-11-24 terminology grill. Shared words live in the root [`CONTEXT.md`](../../../../CONTEXT.md).

## The shape

**Budget year**:
One year's budget ("2027"). It is Draft, then Active, then Closed.

**Archive**:
Hiding a budget year from everyone but admins. Nothing is deleted.
_Avoid_: delete

**Group**:
A set of categories inside a year, such as "Departments" or "Site infrastructure".

**Category**:
One budget inside a group, usually one department's ("Sound").

**Line item**:
One planned income or expense inside a category ("Speakers, €3,000").

**Department**:
A top-level team. Each department gets a category in the Departments group; "Sync departments" adds the missing ones.

## The amounts

Example: Sound gets €5,000, plans speakers €3,000 and cables €500, and Holded shows €2,100 spent.

**Allocated**:
What a category is given (€5,000).

**Planned**:
The total of its line items (€3,500).
_Avoid_: detailed in line items

**Available**:
Allocated minus planned (€1,500): money given but not yet planned. The summary shows it per group as "Available in Departments".
_Avoid_: unallocated, buffer

**Actual**:
What was really spent or earned, from Holded (€2,100).

**Cashflow only**:
A line that moves cash but stays out of income and expense totals, such as a donation.

## Who

**Edit budget**:
The permission to change line items in a department's categories, and every team below it.
_Avoid_: coordinator (Coordinator is a Roles-section role)

**Budget admin**:
The admin pages for years, groups and categories. The FinanceAdmin system role runs Budget, Finance and Holded together: the one exception to one admin per section.
_Avoid_: Finance admin (as the page name)

## Special groups

**Restricted group**:
A group people with Edit budget see only by name; they can't open it.

**Ticketing group**:
A group filled automatically from ticket sales, hidden from people with Edit budget.

**Projection**:
The estimated weekly ticket sales for the weeks ahead.
_Avoid_: forecast

**Actuals**:
The weekly rows of real ticket sales and fees.

**Platform fee**:
The fee the ticketing platform takes per sale.
_Avoid_: TicketTailor fee, vendor fee
