#!/bin/bash
# Freshness check: about-page-packages
#
# Builds the production package set: every PackageVersion Include="..." in
# Directory.Packages.props that is also PackageReference Include="..." in any
# src/**/*.csproj. For each, verifies the package name (or a sensible
# substring alias) appears somewhere in the About page.
#
# Source: Directory.Packages.props × src/**/*.csproj
# Doc:    src/Humans.Web/Views/About/Index.cshtml

set -euo pipefail

DOC="src/Humans.Web/Views/About/Index.cshtml"
PROPS="Directory.Packages.props"

# Packages intentionally excluded from About — design-time-only tools declared
# with <PrivateAssets>all</PrivateAssets> that don't ship in the published
# output. Lowercased package names, one per line.
IGNORE_PACKAGES="microsoft.entityframeworkcore.design"

if [ ! -f "$DOC" ]; then
  echo "FAIL [about-page-packages]: $DOC does not exist"
  exit 1
fi
if [ ! -f "$PROPS" ]; then
  echo "FAIL [about-page-packages]: $PROPS does not exist"
  exit 1
fi

# All declared package versions.
DECLARED=$(grep -oE '<PackageVersion Include="[^"]+"' "$PROPS" | sed 's/.*"\(.*\)"/\1/' | sort -u)

# All package references in src (NOT tests).
PROD_REFS=$(grep -hoE '<PackageReference Include="[^"]+"' src/*/*.csproj 2>/dev/null \
  | sed 's/.*"\(.*\)"/\1/' | sort -u)

# Intersection = production packages we should mention on About.
PROD_PACKAGES=$(comm -12 <(echo "$DECLARED") <(echo "$PROD_REFS"))
PROD_COUNT=$(echo "$PROD_PACKAGES" | grep -cv '^$' || true)

# Lowercase the doc once.
DOC_LOWER=$(tr '[:upper:]' '[:lower:]' < "$DOC")

MISSING=""
MISS_COUNT=0
FOUND_COUNT=0

# alias_for <package-name>: prints space-separated substrings to try.
# Returns the package name itself plus pragmatic OS-aliased fallbacks for
# Microsoft.* / Google.Apis.* packages where the About page typically uses
# a shortened form ("Identity", "Drive API", etc.).
alias_for() {
  local pkg="$1"
  local lower
  lower=$(echo "$pkg" | tr '[:upper:]' '[:lower:]')
  echo "$lower"
  case "$lower" in
    microsoft.aspnetcore.identity.entityframeworkcore) echo "identity.entityframeworkcore"; echo "asp.net core identity" ;;
    microsoft.aspnetcore.authentication.google)        echo "authentication.google"; echo "google authentication" ;;
    microsoft.aspnetcore.dataprotection.entityframeworkcore) echo "dataprotection"; echo "data protection" ;;
    microsoft.aspnetcore.dataprotection.extensions)    echo "dataprotection"; echo "data protection" ;;
    microsoft.aspnetcore.mvc.testing)                  echo "mvc.testing" ;;
    microsoft.entityframeworkcore)                     echo "entityframeworkcore"; echo "entity framework core" ;;
    microsoft.entityframeworkcore.inmemory)            echo "entityframeworkcore.inmemory" ;;
    microsoft.extensions.localization)                 echo "extensions.localization"; echo "localization" ;;
    microsoft.visualstudio.threading.analyzers)        echo "threading.analyzers" ;;
    microsoft.codeanalysis.bannedapianalyzers)         echo "bannedapianalyzers"; echo "banned api" ;;
    microsoft.net.test.sdk)                            echo "test.sdk" ;;
    google.apis.admin.directory.directory_v1)          echo "admin.directory"; echo "directory api" ;;
    google.apis.cloudidentity.v1)                      echo "cloudidentity" ;;
    google.apis.drive.v3)                              echo "drive.v3"; echo "drive api" ;;
    google.apis.driveactivity.v2)                      echo "driveactivity" ;;
    google.apis.groupssettings.v1)                     echo "groupssettings" ;;
    google.apis.auth)                                  echo "google.apis" ;;
    nodatime.serialization.systemtextjson)             echo "nodatime.serialization" ;;
    nodatime.testing)                                  echo "nodatime" ;;
    npgsql.entityframeworkcore.postgresql)             echo "npgsql"; echo "postgresql" ;;
    npgsql.entityframeworkcore.postgresql.nodatime)    echo "npgsql"; echo "nodatime" ;;
    opentelemetry.exporter.opentelemetryprotocol)      echo "opentelemetry" ;;
    opentelemetry.exporter.prometheus.aspnetcore)      echo "opentelemetry"; echo "prometheus" ;;
    opentelemetry.extensions.hosting)                  echo "opentelemetry" ;;
    opentelemetry.instrumentation.aspnetcore)          echo "opentelemetry" ;;
    opentelemetry.instrumentation.entityframeworkcore) echo "opentelemetry" ;;
    opentelemetry.instrumentation.http)                echo "opentelemetry" ;;
    opentelemetry.instrumentation.runtime)             echo "opentelemetry" ;;
    serilog.aspnetcore)                                echo "serilog" ;;
    serilog.sinks.debug)                               echo "serilog" ;;
    serilog.sinks.opentelemetry)                       echo "serilog" ;;
    aspnetcore.healthchecks.npgsql)                    echo "healthchecks" ;;
    aspnetcore.healthchecks.hangfire)                  echo "healthchecks" ;;
    aspnetcore.healthchecks.uris)                      echo "healthchecks" ;;
    hangfire.aspnetcore)                               echo "hangfire" ;;
    hangfire.postgresql)                               echo "hangfire" ;;
    hangfire.core)                                     echo "hangfire" ;;
    magick.net-q8-anycpu)                              echo "magick.net"; echo "imagemagick" ;;
    system.security.cryptography.xml)                  echo "cryptography" ;;
  esac
}

for PKG in $PROD_PACKAGES; do
  PKG_LOWER=$(echo "$PKG" | tr '[:upper:]' '[:lower:]')
  if echo "$IGNORE_PACKAGES" | grep -qx "$PKG_LOWER"; then
    continue
  fi
  FOUND=false
  for ALIAS in $(alias_for "$PKG"); do
    if echo "$DOC_LOWER" | grep -qF "$ALIAS"; then
      FOUND=true
      break
    fi
  done
  if [ "$FOUND" = "true" ]; then
    FOUND_COUNT=$((FOUND_COUNT + 1))
  else
    MISSING="${MISSING}${PKG}
"
    MISS_COUNT=$((MISS_COUNT + 1))
  fi
done

if [ "$MISS_COUNT" -gt 0 ]; then
  echo "FAIL [about-page-packages]: $MISS_COUNT of $PROD_COUNT production packages missing from $DOC"
  echo "$MISSING" | sed 's/^/  - /' | grep -v '^  - $' || true
  exit 1
fi

echo "PASS [about-page-packages]: all $PROD_COUNT production packages referenced in $DOC"
exit 0
