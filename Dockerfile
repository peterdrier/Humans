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

# Coolify passes SOURCE_COMMIT and MINVER_VERSION as build args; deploy-qa.sh sets them from the host repo
ARG SOURCE_COMMIT=""
ARG MINVER_VERSION=""
RUN if [ -n "${MINVER_VERSION}" ]; then \
        dotnet publish src/Humans.Web/Humans.Web.csproj -c Release -o /app/publish \
            -p:TreatWarningsAsErrors=false \
            -p:SourceRevisionId="${SOURCE_COMMIT}" \
            -p:MinVerVersionOverride="${MINVER_VERSION}"; \
    else \
        dotnet publish src/Humans.Web/Humans.Web.csproj -c Release -o /app/publish \
            -p:TreatWarningsAsErrors=false \
            -p:SourceRevisionId="${SOURCE_COMMIT}"; \
    fi

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install native dependencies for SkiaSharp + curl for healthcheck
# (libheif native binaries are provided by the LibHeif.Native NuGet package)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser -s /sbin/nologin appuser

# Copy published files
COPY --from=build /app/publish .

# Ensure App_Data exists (profile picture filesystem store — phase 1 of
# nobodies-collective/Humans#527). In production this directory must be a
# persistent volume (Coolify/preview); otherwise pictures migrated from the
# DB fallback are lost on container restart.
RUN mkdir -p /app/App_Data/profile-pictures

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

# Copy entrypoint wrapper (handles preview environment DB selection)
COPY docker-entrypoint.sh /app/docker-entrypoint.sh

# Entry point
ENTRYPOINT ["/app/docker-entrypoint.sh"]
