# section-doctor — Rideshare — 2026-10-09

- Invocation: unattended daily routine (no arguments); routine prompt: skip Phase 8, PR against peterdrier/Humans main, ignore verify-migrations-apply
- Anchor commit: `a5c17fcdd` (origin/main at branch point); branch `section-doctor/2026-10-09T011615Z`.
- Budget: 2.5h.
- PR: pending

## Assessment summary

## Findings

## Debt verified

## Worked

## Skipped

## Retro

## Needs Peter

## File coverage

| Path | Disposition |
|---|---|
| `src/Sections/Humans.Rideshare/Controllers/RideshareAdminController.cs` |  |
| `src/Sections/Humans.Rideshare/Controllers/RideshareApiController.cs` |  |
| `src/Sections/Humans.Rideshare/Controllers/RideshareController.cs` |  |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareInterestConfiguration.cs` |  |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareRequestConfiguration.cs` |  |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareSettingsConfiguration.cs` |  |
| `src/Sections/Humans.Rideshare/Data/Configurations/RideshareTripConfiguration.cs` |  |
| `src/Sections/Humans.Rideshare/Data/IRideshareRepository.cs` |  |
| `src/Sections/Humans.Rideshare/Data/Migrations/20260909233307_InitialRideshare.Designer.cs` | generated |
| `src/Sections/Humans.Rideshare/Data/Migrations/20260909233307_InitialRideshare.cs` |  |
| `src/Sections/Humans.Rideshare/Data/Migrations/RideshareDbContextModelSnapshot.cs` | generated |
| `src/Sections/Humans.Rideshare/Data/RideshareDbContext.cs` |  |
| `src/Sections/Humans.Rideshare/Data/RideshareDbContextFactory.cs` |  |
| `src/Sections/Humans.Rideshare/Data/RideshareRepository.cs` |  |
| `src/Sections/Humans.Rideshare/Docs/2026-06-14-rideshare-section-design.md` |  |
| `src/Sections/Humans.Rideshare/Docs/Rideshare.md` |  |
| `src/Sections/Humans.Rideshare/Docs/authorization.md` |  |
| `src/Sections/Humans.Rideshare/Docs/data-access.md` |  |
| `src/Sections/Humans.Rideshare/Docs/debt.yml` |  |
| `src/Sections/Humans.Rideshare/Docs/features/rideshare-board.md` |  |
| `src/Sections/Humans.Rideshare/Docs/health.md` |  |
| `src/Sections/Humans.Rideshare/Domain/CostSharing.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/InterestStatus.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/LuggageSize.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/RequestStatus.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/RideshareDirection.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/RideshareInterest.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/RideshareRequest.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/RideshareSettings.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/RideshareTrip.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/TripStatus.cs` |  |
| `src/Sections/Humans.Rideshare/Domain/VehicleType.cs` |  |
| `src/Sections/Humans.Rideshare/Humans.Rideshare.csproj` |  |
| `src/Sections/Humans.Rideshare/Models/AdminViewModels.cs` |  |
| `src/Sections/Humans.Rideshare/Models/BoardFeatureCollection.cs` |  |
| `src/Sections/Humans.Rideshare/Models/BoardViewModel.cs` |  |
| `src/Sections/Humans.Rideshare/Models/MineViewModel.cs` |  |
| `src/Sections/Humans.Rideshare/Models/OfferFormViewModel.cs` |  |
| `src/Sections/Humans.Rideshare/Models/RequestFormViewModel.cs` |  |
| `src/Sections/Humans.Rideshare/Models/RideshareDates.cs` |  |
| `src/Sections/Humans.Rideshare/Properties/AssemblyInfo.cs` |  |
| `src/Sections/Humans.Rideshare/RideshareResource.ca.resx` |  |
| `src/Sections/Humans.Rideshare/RideshareResource.cs` |  |
| `src/Sections/Humans.Rideshare/RideshareResource.de.resx` |  |
| `src/Sections/Humans.Rideshare/RideshareResource.es.resx` |  |
| `src/Sections/Humans.Rideshare/RideshareResource.fr.resx` |  |
| `src/Sections/Humans.Rideshare/RideshareResource.it.resx` |  |
| `src/Sections/Humans.Rideshare/RideshareResource.resx` |  |
| `src/Sections/Humans.Rideshare/Section.cs` |  |
| `src/Sections/Humans.Rideshare/SectionAdminNav.cs` |  |
| `src/Sections/Humans.Rideshare/SectionNav.cs` |  |
| `src/Sections/Humans.Rideshare/SectionPolicies.cs` |  |
| `src/Sections/Humans.Rideshare/Services/AuditEntityTypes.cs` |  |
| `src/Sections/Humans.Rideshare/Services/CachingRideshareService.cs` |  |
| `src/Sections/Humans.Rideshare/Services/IRideshareService.cs` |  |
| `src/Sections/Humans.Rideshare/Services/RideshareReadModels.cs` |  |
| `src/Sections/Humans.Rideshare/Services/RideshareRuleException.cs` |  |
| `src/Sections/Humans.Rideshare/Services/RideshareService.cs` |  |
| `src/Sections/Humans.Rideshare/Services/Routing/IRouteProvider.cs` |  |
| `src/Sections/Humans.Rideshare/Services/Routing/OpenRouteServiceClient.cs` |  |
| `src/Sections/Humans.Rideshare/Services/Routing/RouteProviderOptions.cs` |  |
| `src/Sections/Humans.Rideshare/Services/Routing/RoutingGeometry.cs` |  |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Index.cshtml` |  |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Mine.cshtml` |  |
| `src/Sections/Humans.Rideshare/Views/Rideshare/Offer.cshtml` |  |
| `src/Sections/Humans.Rideshare/Views/Rideshare/RideRequest.cshtml` |  |
| `src/Sections/Humans.Rideshare/Views/Rideshare/_InterestRow.cshtml` |  |
| `src/Sections/Humans.Rideshare/Views/RideshareAdmin/Day.cshtml` |  |
| `src/Sections/Humans.Rideshare/Views/RideshareAdmin/Index.cshtml` |  |
| `src/Sections/Humans.Rideshare/Views/_ViewImports.cshtml` |  |
| `src/Sections/Humans.Rideshare/wwwroot/js/rideshare/board.js` |  |
| `src/Sections/Humans.Rideshare/wwwroot/js/rideshare/pick-point.js` |  |
| `tests/Humans.Rideshare.Tests/Browser/board.test.cjs` |  |
| `tests/Humans.Rideshare.Tests/Browser/pick-point.test.cjs` |  |
| `tests/Humans.Rideshare.Tests/Controllers/RideshareControllerTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Humans.Rideshare.Tests.csproj` |  |
| `tests/Humans.Rideshare.Tests/Infrastructure/RideshareTestHarness.cs` |  |
| `tests/Humans.Rideshare.Tests/Models/BoardAndDayViewModelTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Models/BoardFeatureCollectionTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Models/FormViewModelTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Models/MineViewModelTests.cs` |  |
| `tests/Humans.Rideshare.Tests/RideshareArchitectureTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Services/CachingRideshareServiceTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Services/OpenRouteServiceClientTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Services/RideshareServiceTests.cs` |  |
| `tests/Humans.Rideshare.Tests/Services/RideshareSnapshotTests.cs` |  |

## Threads

| Thread | How it ran | Model | Findings |
|---|---|---|---|
| Shape | main |  |  |
| Behavior & bugs | main |  |  |
| Freshness |  |  |  |
| Conformance |  |  |  |
| Tests |  |  |  |
| Prose & surface |  |  |  |
| History |  |  |  |
| Comments |  |  |  |
| Inbox |  |  |  |

