# Camps — Section Invariants

## Concepts

- A **Camp** (also called "Barrio") is a themed community camp. Each camp has a unique URL slug, one or more leads, and optional images.
- A **Camp Season** is a per-year registration for a camp, containing the year-specific name, description, community info, and placement details.
- A **Camp Lead** is a human responsible for managing a camp. Leads have a role: Primary or CoLead.
- **Camp Settings** is a singleton controlling which year is public (shown in the directory) and which seasons accept new registrations.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Anyone (including anonymous) | Browse the camps directory, view camp details and season details |
| Any authenticated human | Register a new camp (which creates a new season in Pending status) |
| Camp lead | Edit their camp's details, manage season registrations, manage co-leads, upload/manage images, manage historical names |
| CampAdmin, Admin | All camp lead capabilities on all camps. Approve/reject season registrations. Manage camp settings (public year, open seasons, name lock dates). View withdrawn and rejected seasons. Export camp data |
| Admin | Delete camps |

## Invariants

- Each camp has a unique slug used for URL routing.
- Camp season status follows: Pending then Active, Full, Rejected, or Withdrawn. Only CampAdmin can approve or reject a season.
- Only camp leads or CampAdmin can edit a camp.
- Camp images are stored on disk; metadata and display order are tracked per camp.
- Historical names are recorded when a camp is renamed.
- Camp settings control which year is shown publicly and which seasons accept registrations.

## Negative Access Rules

- Regular humans **cannot** edit camps they do not lead.
- Camp leads **cannot** approve or reject season registrations — that requires CampAdmin or Admin.
- CampAdmin **cannot** delete camps. Only Admin can delete a camp.
- Anonymous visitors **cannot** register camps or edit any camp data.

## Triggers

- When a camp is registered, its initial season is created with Pending status.
- Season approval or rejection is performed by CampAdmin.

## Cross-Section Dependencies

- **Profiles**: Camp leads are linked to humans. Lead assignment requires a valid human account.
- **Admin**: Camp settings management is restricted to CampAdmin and Admin.
