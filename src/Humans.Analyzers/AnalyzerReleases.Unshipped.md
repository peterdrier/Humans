### New Rules

Rule ID | Category              | Severity | Notes
--------|-----------------------|----------|----------------------------------------------------------------------
HUM0001 | Humans.Architecture   | Error    | Reference to deleted email-identity-decoupling legacy member
HUM0002 | Humans.Architecture   | Error    | Write to Identity-derived User column from Application or Web
HUM0003 | Humans.Architecture   | Error    | UserManager.FindByEmailAsync / FindByNameAsync called from Application or Web
HUM0005 | Humans.Architecture   | Error    | IUserEmailService.UpdateEmailAsync called from outside AccountController
HUM0006 | Humans.Architecture   | Error    | IUserRepository.ApplyUserEmailReconcilePlanAsync called from outside approved user-email services
HUM0007 | Humans.Architecture   | Error    | Concurrency token metadata is forbidden in live source
HUM0008 | Humans.Architecture   | Error    | Controller constructor injects an application DbContext
HUM0009 | Humans.Architecture   | Error    | Class uses an application DbContext but does not implement IRepository (downgrades to Warning for classes carrying [Grandfathered("HUM0009", ...)])
HUM0010 | Humans.Architecture   | Warning  | Reference to symbol decorated with [ExpiresOn(date)] (escalates to Error on/after the date)
HUM0011 | Humans.Architecture   | Warning  | Declaration decorated with [ExpiresOn(date)] is past its date (escalates to Error after the graceDays window)
HUM0014 | Humans.Architecture   | Error    | Controller injects a repository directly (must go through the section application service)
HUM0015 | Humans.Architecture   | Error    | Type decorated with [SurfaceBudget(N)] declares more than N public-instance methods
HUM0016 | Humans.Architecture   | Error    | Type decorated with [SurfaceBudget(N)] declares fewer than N public-instance methods (slack — decrement budget)
HUM0019 | Humans.Architecture   | Warning  | Read of Identity-derived User column (Email/NormalizedEmail/UserName/NormalizedUserName) from Application or Web
HUM0020 | Humans.Architecture   | Error    | Caching decorator references a repository directly instead of routing through the keyed inner service
HUM0025 | Humans.Architecture   | Error    | A DbSet table is referenced (read or written) by more than one repository — a table must belong to exactly one repository (downgrades to Warning for repos carrying [Grandfathered("HUM0025", ..., scope: "<DbSet>")])
HUM0026 | Humans.Architecture   | Error    | IOrchestrator implementer injects an I*Repository, an application DbContext, or IDbContextFactory<TContext> parameterized on one
HUM0027 | Humans.Architecture   | Error    | Type implements both IApplicationService and IOrchestrator — the role axis is exclusive
HUM0028 | Humans.Architecture   | Error    | Interface extends IInvalidator (downgrades to Warning for interfaces carrying [Grandfathered("HUM0028", ...)])
HUM0030 | Humans.Architecture   | Error    | Date/time format-string literal used outside the single sanctioned home (Humans.Base.Extensions.DateFormattingExtensions) — custom .ToString format, interpolation format clause, or NodaTime *Pattern.Create literal
HUM0031 | Humans.Architecture   | Error    | Controller method exceeds the business-logic thresholds (statements > 40 or cyclomatic complexity > 15) — move the logic into the section's application service (downgrades to Warning for methods carrying [Grandfathered("HUM0031", ...)])
HUM0032 | Humans.Architecture   | Error    | Cross-section injection of a write-capable I*Service that has an I*ServiceRead base — inject the read interface, or mark the class [CrossSectionWrite("reason")]
HUM0034 | Humans.Architecture   | Error    | Public type in a section outside Contracts/ that is not the Section entry point, the <Section>Resource marker, an EF migration, or a framework-required-public type (view component, tag helper)
HUM0035 | Humans.Architecture   | Error    | Repository interface or implementation declared under a section Contracts/ folder
HUM0033 | Humans.Architecture   | Error    | State-changing controller action ([HttpPost]/[HttpPut]/[HttpDelete]/[HttpPatch]) passes a request-scoped cancellation token (HttpContext.RequestAborted or the action's own CancellationToken parameter) to a method marked [ExternalWrite] (downgrades to Warning for actions carrying [Grandfathered("HUM0033", ...)])
