#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: .claude/bootstrap-dotnet.sh [options]

Brings a fresh cloud container up to a working .NET toolchain for this repo,
then reports honestly which capabilities the result actually has. Safe to run
on a machine that is already set up — every step is a no-op when satisfied.

What it does, in order:
  1. Ensures a .NET SDK matching global.json is on PATH. Prefers the official
     installer when its host is reachable (that channel carries every feature
     band); falls back to the distro package when it is not.
  2. Puts the global tool directory on PATH.
  3. Restores the pinned local tools (Stryker, from .config/dotnet-tools.json).
  4. Installs Reforge as a global tool.
  5. Probes a real build and reports whether full build/test/mutation work is
     available, or only the read-and-write-docs subset.

Exit status is 0 whenever the SDK is present, even if the build probe fails —
a degraded environment is a fact to report, not an error to abort on. It exits
non-zero only when there is no usable SDK at all.

Options:
  --no-probe       Skip the build probe (saves ~1 minute; capability unknown)
  --no-tools       Skip Stryker and Reforge
  --quiet          Only print the final summary
  --help           Show this help

Examples:
  .claude/bootstrap-dotnet.sh
  .claude/bootstrap-dotnet.sh --no-probe --quiet
EOF
}

PROBE=1
TOOLS=1
QUIET=0

while [ $# -gt 0 ]; do
  case "$1" in
    --no-probe) PROBE=0 ;;
    --no-tools) TOOLS=0 ;;
    --quiet)    QUIET=1 ;;
    --help|-h)  usage; exit 0 ;;
    *) echo "bootstrap-dotnet: unknown option '$1'" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

say() { [ "$QUIET" = "1" ] || echo "bootstrap-dotnet: $*"; }
warn() { echo "bootstrap-dotnet: $*" >&2; }

# ── What this repo asks for ───────────────────────────────────────────────────
# Read the requirement rather than hardcoding it, so a global.json bump does not
# silently leave this script installing last year's SDK.
SDK_VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9][^"]*\)".*/\1/p' global.json | head -1)"
[ -n "$SDK_VERSION" ] || { warn "could not read sdk.version from global.json"; exit 1; }
SDK_MAJOR_MINOR="${SDK_VERSION%.*}"          # 10.0.100 -> 10.0
say "global.json wants SDK $SDK_VERSION (channel $SDK_MAJOR_MINOR)"

SDK_SOURCE="already present"

# ── 1. The SDK ────────────────────────────────────────────────────────────────
if ! command -v dotnet >/dev/null 2>&1; then
  # The official installer is preferred: it serves every feature band, so it can
  # satisfy analyzer packages that need a newer Roslyn than a distro build ships.
  # Some environments deny its host by egress policy. Probe once and believe the
  # answer — a policy denial is not something to retry or route around.
  INSTALLER_HOST="https://builds.dotnet.microsoft.com"
  say "no dotnet on PATH; probing $INSTALLER_HOST"
  if curl -fsS --max-time 20 -o /dev/null "$INSTALLER_HOST/dotnet/release-metadata/releases-index.json" 2>/dev/null; then
    say "installer host reachable; installing channel $SDK_MAJOR_MINOR"
    curl -fsSL --max-time 120 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel "$SDK_MAJOR_MINOR" --install-dir "$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
    SDK_SOURCE="official installer"
  elif command -v apt-get >/dev/null 2>&1; then
    say "installer host unreachable (egress policy or offline); falling back to the distro package"
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "dotnet-sdk-${SDK_MAJOR_MINOR}"
    SDK_SOURCE="distro package"
  else
    warn "no dotnet, no reachable installer, and no apt-get — cannot bootstrap"
    exit 1
  fi
fi

command -v dotnet >/dev/null 2>&1 || { warn "dotnet still not on PATH after install"; exit 1; }
SDK_ACTUAL="$(dotnet --version 2>/dev/null || echo unknown)"
say "SDK $SDK_ACTUAL ($SDK_SOURCE)"

# ── 2. Global tools on PATH ───────────────────────────────────────────────────
# dotnet puts global tools here and says so on first install, but nothing adds it
# to PATH for a non-interactive shell.
export PATH="$PATH:$HOME/.dotnet/tools"

# ── 3 & 4. Tools ──────────────────────────────────────────────────────────────
STRYKER_STATUS="skipped"
REFORGE_STATUS="skipped"
if [ "$TOOLS" = "1" ]; then
  if dotnet tool restore >/dev/null 2>&1; then
    STRYKER_STATUS="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' .config/dotnet-tools.json | head -1)"
    STRYKER_STATUS="${STRYKER_STATUS:-restored}"
  else
    STRYKER_STATUS="FAILED"
    warn "dotnet tool restore failed — mutation testing will not be available"
  fi

  if dotnet tool update --global Reforge >/dev/null 2>&1; then
    # `reforge --version` appends a build hash; the version alone is what reads well.
    REFORGE_STATUS="$(reforge --version 2>/dev/null | head -1 | cut -d+ -f1 || echo installed)"
  else
    REFORGE_STATUS="FAILED"
    warn "Reforge install failed — surface scores will not be available"
  fi
fi

# ── 5. Does a build actually work here? ───────────────────────────────────────
# An SDK on PATH is not the same as a build. This repo's analyzers are compiled
# against a pinned Roslyn, and an SDK whose compiler is older than that pin fails
# every project with CS9057 before a single line is compiled. Find out now, with
# one small real build, rather than three phases into a run.
BUILD_STATUS="not probed"
BUILD_DETAIL=""
if [ "$PROBE" = "1" ]; then
  say "probing a real build (this takes about a minute)"
  PROBE_LOG="$(mktemp)"
  PROBE_PROJECT="src/Humans.Analyzers/Humans.Analyzers.csproj"
  if [ ! -f "$PROBE_PROJECT" ]; then
    PROBE_PROJECT="Humans.slnx"
  fi
  if dotnet build "$PROBE_PROJECT" -v quiet >"$PROBE_LOG" 2>&1 \
     && dotnet build src/Humans.Base/Humans.Base.csproj -v quiet >>"$PROBE_LOG" 2>&1; then
    BUILD_STATUS="yes"
  elif grep -q CS9057 "$PROBE_LOG"; then
    PINNED="$(sed -n 's/.*Microsoft\.CodeAnalysis\.CSharp"[[:space:]]*Version="\([^"]*\)".*/\1/p' Directory.Packages.props | head -1)"
    BUILD_STATUS="NO — CS9057"
    BUILD_DETAIL="the analyzers target Roslyn ${PINNED:-(pinned in Directory.Packages.props)}, newer than this SDK's compiler.
               -p:RunAnalyzers=false does NOT bypass it: the assembly is loaded
               and version-checked before it is asked to run.
               Fix one of: allow builds.dotnet.microsoft.com for this environment;
               bake a matching SDK into the image; or pin
               Microsoft.CodeAnalysis.CSharp down to the band this SDK ships.
               Until then: no build, no test, no Stryker, no reforge score. Work
               the reading threads, keep changes to docs and comments, queue every
               code finding, and let CI be the compile gate."
  else
    BUILD_STATUS="NO — see log"
    BUILD_DETAIL="build failed for a reason other than CS9057; log at $PROBE_LOG"
  fi
fi

# ── Summary ───────────────────────────────────────────────────────────────────
cat <<EOF

bootstrap-dotnet: summary
  SDK          ${SDK_ACTUAL} (${SDK_SOURCE}), global.json wants ${SDK_VERSION}
  Stryker      ${STRYKER_STATUS}
  Reforge      ${REFORGE_STATUS}
  Full build   ${BUILD_STATUS}
EOF
if [ -n "$BUILD_DETAIL" ]; then
  echo "               ${BUILD_DETAIL}"
fi
cat <<'EOF'

  Add the global tool dir to PATH in your own shell:
      export PATH="$PATH:$HOME/.dotnet/tools"
EOF
