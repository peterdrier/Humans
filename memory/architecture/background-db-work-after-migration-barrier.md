---
name: Background DB workers arm behind the migration barrier
description: timers/pollers/pre-warmers that query the DB must arm in IHostedService.StartAsync — never a constructor Timer or an eager GetRequiredService before app.Run() — so they run after DatabaseMigrationHostedService applies migrations
---

Any background worker that touches the database (refresh timers, pollers, cache pre-warmers) must arm/start its work from `IHostedService.StartAsync`, **not** from a constructor and **not** from an eager `GetRequiredService<T>()` before `app.Run()`. The host runs every `IHostedLifecycleService.StartingAsync` — including `DatabaseMigrationHostedService`, which applies pending migrations — to completion before *any* `StartAsync`. Arming in the constructor escapes that barrier and can query a not-yet-migrated schema.

**Why:** Incident on the #804 prod deploy (2026-05-25). `HumansMetricsService` armed a `Timer(dueTime: TimeSpan.Zero)` in its constructor, eager-resolved in `Program.cs` before `app.Run()`. The immediate first tick raced the `MoveDietaryMedicalToProfile` migration's `ADD COLUMN` and ran `SELECT … p."Allergies" … FROM profiles` before the column existed → `42703 column p.Allergies does not exist`. It was non-fatal (caught, retried in 60 s) but produced an alarming production error and looked exactly like impossible schema/history drift, costing real triage time. Note HTTP requests never hit it — Kestrel only starts listening in `StartAsync`, already behind the barrier — so the constructor-armed timer was the *only* thing that escaped.

**How to apply:**

- Register the worker with `AddHostedService`. If it's also injected elsewhere as a singleton, point the hosted registration at the same instance: `services.AddHostedService(sp => (TImpl)sp.GetRequiredService<IFace>())` (see `HttpStatusTracker` and `HumansMetricsService` in `TelemetryInfrastructureExtensions`).
- Arm timers in `StartAsync`; stop in `StopAsync` and dispose in `Dispose`. `dueTime: Zero` is fine *there* — the barrier has already passed.
- Do **not** arm a DB-touching timer in a constructor.
- Do **not** add an eager `GetRequiredService<T>()` before `app.Run()` to "start a background timer immediately."

**Related:** [`no-startup-guards`](no-startup-guards.md) (the worker must still never block boot — degrade and retry), [`crosscut-purity`](crosscut-purity.md) (Metrics/Audit/Email/Notification are crosscuts).
