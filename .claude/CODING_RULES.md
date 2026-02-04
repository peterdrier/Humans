# Coding Rules

## Critical: Do Not Remove "Unused" Properties

Properties/methods appearing "unused" may be used dynamically via reflection:
- Serialization/deserialization
- Change tracking
- Object cloning/merging
- Dynamic binding

**Rule:** Do not remove properties/methods that appear unused without verifying they're not used via reflection.

## Critical: Never Rename Fields in Serialized Objects

Classes that are JSON serialized (to databases, APIs, files) will break if properties are renamed. Existing JSON expects the current property names.

**Rule:** Never rename properties on serialized classes. Existing data expects the current property names.

**Example:**
```csharp
// WRONG - breaks existing data
public class User {
    public string UserName { get; set; }  // Renamed from "Name"
}

// CORRECT - keeps existing property name
public class User {
    public string Name { get; set; }  // Matches JSON in storage
}
```

**Exceptions:**
- Adding `[JsonIgnore]` computed properties is safe (they're not serialized)
- Adding new properties is safe (old records will use default values)

## JSON Serialization

Uses System.Text.Json.

**Required attributes:**
- Private setters: `[JsonInclude]`
- New data classes: `[JsonConstructor]` (private parameterless)
- Polymorphic types: `[JsonPolymorphic]` + `[JsonDerivedType]` on base class

**Example:**
```csharp
public class MyData {
    [JsonInclude]
    public string PrivateProp { get; private set; }

    [JsonConstructor]
    private MyData() { }
}
```

## Timezone Handling

**Prefer NodaTime for internal time handling:**
- Use NodaTime types (`Instant`, `LocalDate`, `ZonedDateTime`) instead of `DateTime`/`DateOnly`/`TimeOnly`

**Server-side ALWAYS uses UTC:**
- Use NodaTime `Instant` or `SystemClock.Instance.GetCurrentInstant()` for current time
- Store all dates/times in UTC (database, JSON, APIs)
- Never store or transmit local timezones from server
- All server-side calculations and comparisons in UTC

**Client-side translates to local time at display:**
- Convert UTC to user's local timezone only at final display step
- Never send local times back to server - convert to UTC first

**Rationale:** NodaTime provides safer time handling. Prevents timezone bugs, ensures consistent server behavior across deployments, simplifies testing.

## String Comparisons

Always use explicit `StringComparison` parameter.

**Rule:** Use `StringComparison.Ordinal` for exact matches, `StringComparison.OrdinalIgnoreCase` for case-insensitive.

**Example:**
```csharp
// WRONG
if (status == "submitted")

// CORRECT
if (string.Equals(status, "submitted", StringComparison.Ordinal))
```

## Build Commands

- Build: `dotnet build`
- Test: `dotnet test`
- Run: `dotnet run`
