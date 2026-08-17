# Scope the inner loop to the section's test project

While iterating on a single section, build and test only that section's test project — `dotnet test tests/Humans.<Section>.Tests -v quiet` builds the dependency closure and runs in seconds, vs minutes for the full solution. Run the full `dotnet test Humans.slnx -v quiet` gate once, before commit/PR — never skip it.

If the change touches cross-section surface (contracts, interfaces, Base/Shell), also run `tests/Humans.Application.Tests` in the inner loop — the architecture tests live there.
