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
HUM0015 | Humans.Architecture   | Error    | Type decorated with [SurfaceBudget(N)] declares more than N public-instance methods
HUM0016 | Humans.Architecture   | Error    | Type decorated with [SurfaceBudget(N)] declares fewer than N public-instance methods (slack — decrement budget)
