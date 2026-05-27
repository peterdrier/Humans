### New Rules

Rule ID | Category              | Severity | Notes
--------|-----------------------|----------|----------------------------------------------------------------------
HUM0001 | Humans.Architecture   | Error    | Reference to deleted email-identity-decoupling legacy member
HUM0002 | Humans.Architecture   | Error    | Write to Identity-derived User column from Application or Web
HUM0003 | Humans.Architecture   | Error    | UserManager.FindByEmailAsync / FindByNameAsync called from Application or Web
HUM0004 | Humans.Architecture   | Error    | Profile.IsSuspended written outside allowlisted dual-writers
HUM0005 | Humans.Architecture   | Error    | IUserEmailService.UpdateEmailAsync called from outside AccountController
HUM0006 | Humans.Architecture   | Error    | IUserRepository.ApplyUserEmailReconcilePlanAsync called from outside approved user-email services
HUM0007 | Humans.Architecture   | Error    | Concurrency token metadata is forbidden in live source
HUM0008 | Humans.Architecture   | Error    | Controller constructor injects HumansDbContext
HUM0009 | Humans.Architecture   | Error    | Class uses HumansDbContext but does not implement IRepository (downgrades to Warning for classes carrying [Grandfathered("HUM0009", ...)])
HUM0010 | Humans.Architecture   | Warning  | Reference to symbol decorated with [ExpiresOn(date)] (escalates to Error on/after the date)
HUM0011 | Humans.Architecture   | Warning  | Declaration decorated with [ExpiresOn(date)] is past its date (escalates to Error after the graceDays window)
HUM0012 | Humans.Architecture   | Error    | Application service (IApplicationService implementer) declared outside Humans.Application.Services.* namespace
HUM0013 | Humans.Architecture   | Error    | Repository interface (IRepository extender) declared outside Humans.Application.Interfaces.Repositories namespace
HUM0014 | Humans.Architecture   | Error    | Class in Humans.Web injects a repository directly (must go through an application service)
HUM0015 | Humans.Architecture   | Error    | Type decorated with [SurfaceBudget(N)] declares more than N public-instance methods
HUM0016 | Humans.Architecture   | Error    | Type decorated with [SurfaceBudget(N)] declares fewer than N public-instance methods (slack — decrement budget)
HUM0017 | Humans.Architecture   | Warning  | Application service injects a repository whose [Section] differs from the service's namespace section
HUM0018 | Humans.Architecture   | Warning  | Section-aware analyzer (e.g. HUM0017) cannot determine the section of a type — missing [Section] or unsection'd namespace
HUM0019 | Humans.Architecture   | Warning  | Read of Identity-derived User column (Email/NormalizedEmail/UserName/NormalizedUserName) from Application or Web
HUM0020 | Humans.Architecture   | Error    | Caching decorator references a repository directly instead of routing through the keyed inner service
HUM0021 | Humans.Architecture   | Warning  | Read of obsolete cross-domain navigation property from Application, Web, or Infrastructure
HUM0024 | Humans.Architecture   | Error    | EF configuration creates a navigation join across section boundaries (downgrades to Warning for classes carrying [Grandfathered("HUM0024", ...)])
HUM0025 | Humans.Architecture   | Error    | A DbSet table is referenced (read or written) by more than one repository — a table must belong to exactly one repository (downgrades to Warning for repos carrying [Grandfathered("HUM0025", ..., scope: "<DbSet>")])
HUM0026 | Humans.Architecture   | Error    | IOrchestrator implementer injects an I*Repository, HumansDbContext, or IDbContextFactory<HumansDbContext>
HUM0027 | Humans.Architecture   | Error    | Type implements both IApplicationService and IOrchestrator — the role axis is exclusive
HUM0028 | Humans.Architecture   | Error    | Interface extends IInvalidator (downgrades to Warning for interfaces carrying [Grandfathered("HUM0028", ...)])
HUM0029 | Humans.Architecture   | Error    | Cross-section read interface (I*Read) exposes EF entity, Microsoft.EntityFrameworkCore type, or System.Linq.IQueryable in a method signature (downgrades to Warning for interfaces carrying [Grandfathered("HUM0029", ...)])
