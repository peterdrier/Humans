# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for restore
COPY .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/Humans.Domain/Humans.Domain.csproj src/Humans.Domain/
COPY src/Humans.Application/Humans.Application.csproj src/Humans.Application/
COPY src/Humans.Infrastructure/Humans.Infrastructure.csproj src/Humans.Infrastructure/
COPY src/Humans.Web/Humans.Web.csproj src/Humans.Web/

# Restore packages
RUN dotnet restore src/Humans.Web/Humans.Web.csproj

# Copy source code
COPY src/ src/

# Coolify passes SOURCE_COMMIT as a build arg; fall back to git rev-parse for local builds
ARG SOURCE_COMMIT=""
RUN dotnet publish src/Humans.Web/Humans.Web.csproj -c Release -o /app/publish --no-restore \
    -p:TreatWarningsAsErrors=false \
    -p:SourceRevisionId="${SOURCE_COMMIT:-$(git rev-parse --short HEAD 2>/dev/null || true)}"

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install native dependencies for SkiaSharp + curl for healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    curl \
    && rm -rf /var/lib/apt/lists/*

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

# Health check using the liveness endpoint (aspnet:10.0 is Debian-based, curl available via apt)
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

# Entry point
ENTRYPOINT ["dotnet", "Humans.Web.dll"]
