# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the whole source tree, then restore. This trades the restore layer's cache
# granularity (any source edit now re-runs restore) for a Dockerfile that never
# needs a per-project COPY line — the project count is heading from 9 to ~40 as
# G5 (nobodies-collective/Humans#866) peels sections into their own projects.
COPY .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/ src/

# Restore packages
RUN dotnet restore src/Humans.Web/Humans.Web.csproj

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
# postgresql-client-18 provides the pg_dump the pre-migration snapshot shells out to
# (nobodies-collective/Humans#845). The client major must be >= the *server* major:
# pg_dump refuses outright to dump a server newer than itself, and production runs
# Postgres 18. It reads older servers fine, so this one client covers the postgres:16
# in docker-compose.yml too — pin it to production's major, never to compose's.
# Noble's own archive stops at 16, so the client comes from PGDG.
# Swap to nl.archive.ubuntu.com — geographically closer and avoids archive.ubuntu.com flakiness
RUN sed -i 's|archive\.ubuntu\.com|nl.archive.ubuntu.com|g; s|security\.ubuntu\.com|nl.archive.ubuntu.com|g' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update && apt-get install -y --no-install-recommends \
        libfontconfig1 \
        curl \
        ca-certificates \
        gnupg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        | gpg --dearmor -o /usr/share/keyrings/pgdg.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update && apt-get install -y --no-install-recommends \
        postgresql-client-18 \
    && apt-get purge -y --auto-remove gnupg \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user and give it ownership of the app directory.
# db-snapshots is created here so a named volume mounted over it inherits appuser
# ownership — a bind mount would land as root and the non-root app could not write.
RUN groupadd -r appuser && useradd -r -g appuser -s /sbin/nologin appuser \
    && mkdir -p /app/db-snapshots \
    && chown appuser:appuser /app /app/db-snapshots

# Copy published files pre-owned by appuser (a separate chown -R would re-run on
# every build and duplicate the entire layer)
COPY --from=build --chown=appuser:appuser /app/publish .

# Switch to non-root user
USER appuser

# Expose ports
EXPOSE 8080
EXPOSE 9090

# Health check using the liveness endpoint (aspnet:10.0 is Debian-based, curl available via apt)
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

# Copy entrypoint wrapper (handles preview environment DB selection)
COPY --chown=appuser:appuser docker-entrypoint.sh /app/docker-entrypoint.sh

# Entry point
ENTRYPOINT ["/app/docker-entrypoint.sh"]
