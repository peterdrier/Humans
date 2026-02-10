# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for restore
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Humans.Domain/Humans.Domain.csproj src/Humans.Domain/
COPY src/Humans.Application/Humans.Application.csproj src/Humans.Application/
COPY src/Humans.Infrastructure/Humans.Infrastructure.csproj src/Humans.Infrastructure/
COPY src/Humans.Web/Humans.Web.csproj src/Humans.Web/

# Restore packages
RUN dotnet restore src/Humans.Web/Humans.Web.csproj

# Copy source code
COPY src/ src/

# Build and publish
RUN dotnet publish src/Humans.Web/Humans.Web.csproj -c Release -o /app/publish --no-restore -p:TreatWarningsAsErrors=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser -s /sbin/nologin appuser

# Copy published files
COPY --from=build /app/publish .

# Set ownership
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose ports
EXPOSE 8080
EXPOSE 9090

# Entry point
ENTRYPOINT ["dotnet", "Humans.Web.dll"]
