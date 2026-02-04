# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY Profiles.slnx .
COPY src/Profiles.Domain/Profiles.Domain.csproj src/Profiles.Domain/
COPY src/Profiles.Application/Profiles.Application.csproj src/Profiles.Application/
COPY src/Profiles.Infrastructure/Profiles.Infrastructure.csproj src/Profiles.Infrastructure/
COPY src/Profiles.Web/Profiles.Web.csproj src/Profiles.Web/

# Restore packages
RUN dotnet restore Profiles.slnx

# Copy source code
COPY src/ src/

# Build and publish
RUN dotnet publish src/Profiles.Web/Profiles.Web.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user
RUN adduser --disabled-password --gecos '' appuser

# Copy published files
COPY --from=build /app/publish .

# Set ownership
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose ports
EXPOSE 8080
EXPOSE 9090

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health/live || exit 1

# Entry point
ENTRYPOINT ["dotnet", "Profiles.Web.dll"]
