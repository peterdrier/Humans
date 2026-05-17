### New Rules

Rule ID | Category              | Severity | Notes
--------|-----------------------|----------|----------------------------------------------------------------------
HUM0001 | Humans.Architecture   | Error    | Reference to deleted email-identity-decoupling legacy member
HUM0002 | Humans.Architecture   | Error    | Write to Identity-derived User column from Application or Web
HUM0003 | Humans.Architecture   | Error    | UserManager.FindByEmailAsync / FindByNameAsync called from Application or Web
HUM0004 | Humans.Architecture   | Error    | Profile.IsSuspended written outside allowlisted dual-writers
HUM0005 | Humans.Architecture   | Error    | IUserEmailService.UpdateEmailAsync called from outside AccountController
HUM0006 | Humans.Architecture   | Error    | IUserEmailRepository.UpdateEmailAsync called from outside UserEmailService
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
