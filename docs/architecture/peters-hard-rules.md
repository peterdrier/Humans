## Hand written rules from Peter

These superceed all other docs and are the final word on how to write code in this codebase. They are not to ever be edited by an LLM, and any changes to them must be made by Peter himself.

* The application is split into a number of vertical sections, each with its own domain model and database tables. Each section is responsible for its own data integrity and invariants, and **must** not reach into other sections' data or logic. Sections may only interact through well-defined interfaces and APIs. 
* There are horizontal sections for cross-cutting concerns like Auth, and Audit.  They are strictly forbidden from referencing vertical sections beyond their current state as that will cause loops in the call graph. 
* The application layers from my pov, are DbContext → Repository → Service → Controller. The DbContext is the lowest-level data access layer, and the Controller is the highest-level presentation layer. Each layer may only call the layer directly below it, and may not skip layers.  Calls between sections may happen at the services layer, and must be via the I<section>ServiceRead when available.
* Repositories must derive from IRepository, and only the repository may read or write to its section's tables. No other class may reference the DbContext directly or indirectly. 
* A table must only exist in one repository. 
* Services must derive from IApplicationService.  
* Services may only call repositories on their own section, and may only call other services through their public interfaces. No service may reach into another section's repository or internal logic.
* Some services are orchestrators, organizing calls to multiple services. These should not call repositories.  
* Controllers should not contain any logic beyond parsing the request, calling the appropriate service(s), and formatting the response. They can not call repositories.  They are responsible for formatting, sorting, filtering.  
* CachingDecorators may not call repositories directly. They must call the inner service via the interface, and the inner service is responsible for calling the repository. This ensures that all calls to the repository go through the service layer, and that caching decorators do not bypass any business logic in the service.

## Preferences
* Analyzers are preferred for enforcing call-site rules because they provide in-editor feedback and precise source locations. Tests are **not acceptable** for rules that fit the analyzer pattern, such as "no new violations from here" baselines. 
* Reuse existing code/services/patterns where possible, rather than creating new ones. That said, do not reuse code, interfaces or patterns that violate these rules or are marked as tech debt. 

## Patterns
* IServiceRead interfaces for cross-section service calls. These are read-only interfaces that expose only the methods needed for other sections to call into the service, and they are implemented by the service itself. They serve as a clear contract for cross-section interactions and help prevent accidental coupling.

## Tech debt
- We have some existing cross-section references in the codebase that violate these rules. They are tech debt and should be refactored to comply with the rules over time. The presence of these references does not justify new ones.

### Examples of existing tech debt:
* Everything in the Humans.Application.Tests/Architecture/Baselines folder - these are the existing violations of the rules that we are aware of and have documented. They should be used as a reference for what not to do, and as a starting point for refactoring to eliminate these violations over time.
* Anything labeled with the GrandfatheredAttribute - these are known violations that we have explicitly allowed to exist for now, but they should be refactored to comply with the rules when possible. The attribute serves as a marker for technical debt that needs to be addressed.
* Anything marked Obsolete

