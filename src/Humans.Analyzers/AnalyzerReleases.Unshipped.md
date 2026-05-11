### New Rules

Rule ID | Category              | Severity | Notes
--------|-----------------------|----------|----------------------------------------------------------------------
HUM0001 | Humans.Architecture   | Error    | Reference to deleted email-identity-decoupling legacy member
HUM0002 | Humans.Architecture   | Error    | Write to Identity-derived User column from Application or Web
HUM0003 | Humans.Architecture   | Error    | UserManager.FindByEmailAsync / FindByNameAsync called from Application or Web
HUM0004 | Humans.Architecture   | Error    | Profile.IsSuspended written outside allowlisted dual-writers
HUM0005 | Humans.Architecture   | Error    | IUserEmailService.UpdateEmailAsync called from outside AccountController
HUM0006 | Humans.Architecture   | Error    | IUserEmailRepository.UpdateEmailAsync called from outside UserEmailService
