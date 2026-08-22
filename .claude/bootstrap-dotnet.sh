#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: .claude/bootstrap-dotnet.sh [options]

Makes a container that has no .NET SDK usable, and gets out of the way
everywhere else. Safe and near-free to call at the top of every run.

On a machine that already has the toolchain — any local checkout, or a cloud
container bootstrapped earlier — it costs about a second and changes nothing.
Every step is skipped when it is already satisfied:

  1. Installs a .NET SDK matching global.json when `dotnet` is absent. Prefers
     the official installer when its host is reachable (that channel carries
     every feature band); falls back to the distro package when it is not.
  2. Puts the global tool directory on PATH.
  3. Restores Stryker (a no-op, and near-instant, once restored).
  4. Installs Reforge only if it is not already on PATH.
  5. Probes one real build ONLY after a fallback SDK install — the one case
     where it is genuinely unknown whether the SDK can compile this repo — and
     caches the verdict for the life of the container. This is the only step
     that costs real time, it never runs on a machine that already had an SDK,
     and it runs at most once per container. `--probe` forces it.

Exit status is 0 whenever an SDK is present, even if the build probe says the
SDK cannot build this repo — a degraded environment is a fact to report, not
an error to abort on. It exits non-zero only when there is no usable SDK.

Options:
  --probe          Force the build probe even if a verdict is cached
  --no-probe       Never probe (leaves capability unknown after a fresh install)
  --no-tools       Skip Stryker and Reforge
  --quiet          Only print the summary
  --help           Show this help

Examples:
  .claude/bootstrap-dotnet.sh
  .claude/bootstrap-dotnet.sh --probe        # re-check after changing SDKs
EOF
}

PROBE=auto
TOOLS=1
QUIET=0

while [ $# -gt 0 ]; do
  case "$1" in
    --probe)    PROBE=force ;;
    --no-probe) PROBE=never ;;
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

# Verdict cache. Outside the repo (never committed, survives worktree churn) and
# inside the container, so a fresh container re-probes and a warm one does not.
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/humans-bootstrap-dotnet"
# VERDICT_FILE is set once the SDK is known: the verdict is a property of an
# SDK/analyzer-pin pair, so keying on them means installing a different SDK or
# repinning Roslyn invalidates it instead of silently reusing a stale answer.
VERDICT_FILE=""

# The environment a *later* command will get. Everything below adds $HOME dirs
# to this script's own PATH, which makes `command -v` answer yes for tools the
# next shell cannot see — so reachability has to be judged against this copy,
# never against the live PATH.
CALLER_PATH="$PATH"

export PATH="$PATH:$HOME/.dotnet/tools"

# A $HOME SDK from an earlier run gets to go first only if it actually works.
# Prepending it merely because the directory exists lets a stale or wrong-band
# muxer shadow every SDK behind it — including one this script is about to
# install under the system prefix, which would then be reported as "still no
# SDK" while sitting perfectly usable on disk. Ask the muxer directly; we are
# already in the repo root, so this resolves global.json.
if [ -x "$HOME/.dotnet/dotnet" ] && "$HOME/.dotnet/dotnet" --version >/dev/null 2>&1; then
  export PATH="$HOME/.dotnet:$PATH"
fi

# Anything installed under $HOME has to be reachable from the *next* command,
# not just this one. A cloud container runs each tool call as a fresh
# non-interactive shell and sources neither ~/.bashrc nor ~/.profile, so an
# `export PATH` here dies with this process and the caller's `dotnet build`
# still fails. Link into a directory that is already on PATH instead.
LINK_DIR=""
for d in /usr/local/bin /usr/bin; do
  case ":$PATH:" in *":$d:"*) [ -w "$d" ] && { LINK_DIR="$d"; break; } ;; esac
done

# Nothing to link is the normal case (a distro SDK is already on PATH), so this
# never fails the run.
link_onto_path() {  # <absolute-target> <command-name>
  if [ -n "$LINK_DIR" ] && [ -x "$1" ] && [ ! -e "$LINK_DIR/$2" ]; then
    ln -s "$1" "$LINK_DIR/$2" || return 0
  fi
  return 0
}

# ── What this repo asks for ───────────────────────────────────────────────────
# Read the requirement rather than hardcoding it, so a global.json bump does not
# silently leave this script installing last year's SDK.
SDK_VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9][^"]*\)".*/\1/p' global.json | head -1)"
[ -n "$SDK_VERSION" ] || { warn "could not read sdk.version from global.json"; exit 1; }
SDK_MAJOR_MINOR="${SDK_VERSION%.*}"          # 10.0.100 -> 10.0

SDK_SOURCE="already present"

# `dotnet` on PATH is not the same as an SDK this repo can use: the host is
# happy to exist while global.json resolves to nothing (a .NET 9 SDK against a
# 10.0 requirement, say). `dotnet --version` run from the repo root does the
# real resolution, so ask it rather than asking whether the binary exists.
usable_sdk() { command -v dotnet >/dev/null 2>&1 && dotnet --version >/dev/null 2>&1; }

# ── 1. The SDK ────────────────────────────────────────────────────────────────
if ! usable_sdk; then
  # The official installer is preferred: it serves every feature band, so it can
  # satisfy analyzer packages that need a newer Roslyn than a distro build ships.
  # Some environments deny its host by egress policy. Probe once and believe the
  # answer — a policy denial is not something to retry or route around.
  INSTALLER_HOST="https://builds.dotnet.microsoft.com"
  if command -v dotnet >/dev/null 2>&1; then
    say "dotnet is on PATH but no SDK satisfies global.json ($SDK_VERSION) — probing $INSTALLER_HOST"
  else
    say "no dotnet on PATH; global.json wants $SDK_VERSION — probing $INSTALLER_HOST"
  fi
  if curl -fsS --max-time 20 -o /dev/null "$INSTALLER_HOST/dotnet/release-metadata/releases-index.json" 2>/dev/null; then
    say "installer host reachable; installing channel $SDK_MAJOR_MINOR"
    curl -fsSL --max-time 120 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel "$SDK_MAJOR_MINOR" --install-dir "$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
    link_onto_path "$HOME/.dotnet/dotnet" dotnet
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

usable_sdk || { warn "still no SDK satisfying global.json ($SDK_VERSION) after install"; exit 1; }
SDK_ACTUAL="$(dotnet --version)"

ROSLYN_PIN="$(sed -n 's/.*Microsoft\.CodeAnalysis\.CSharp"[[:space:]]*Version="\([^"]*\)".*/\1/p' Directory.Packages.props | head -1)"
VERDICT_FILE="$CACHE_DIR/verdict-sdk${SDK_ACTUAL}-roslyn${ROSLYN_PIN:-none}"

# ── 2 & 3. Tools, each only if missing ────────────────────────────────────────
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

  if command -v reforge >/dev/null 2>&1 || dotnet tool update --global Reforge >/dev/null 2>&1; then
    # `reforge --version` appends a build hash; the version alone is what reads well.
    REFORGE_STATUS="$(reforge --version 2>/dev/null | head -1 | cut -d+ -f1 || echo present)"
  else
    REFORGE_STATUS="FAILED"
    warn "Reforge install failed — surface scores will not be available"
  fi
fi

# Link what ended up under $HOME onto PATH, whether this run installed it or
# found it: `command -v` above is answered by this script's own PATH export,
# which the caller's next shell will not have.
#
# The dotnet muxer gets linked only if it passes the same usability check that
# decided whether to trust it earlier. Linking one we rejected would put it at
# /usr/local/bin/dotnet — ahead of /usr/bin — and hand the next shell exactly
# the broken SDK this script just worked around.
if [ -x "$HOME/.dotnet/dotnet" ] && "$HOME/.dotnet/dotnet" --version >/dev/null 2>&1; then
  link_onto_path "$HOME/.dotnet/dotnet" dotnet
fi
link_onto_path "$HOME/.dotnet/tools/reforge" reforge

# ── 4. Can this SDK actually build the repo? ──────────────────────────────────
# Only an open question after a fallback install: this repo's analyzers are
# compiled against a pinned Roslyn, and an SDK whose compiler is older fails
# every project with CS9057 before a line is compiled. Ask once, cache the
# answer, and never spend the minute again in this container.
BUILD_STATUS="not probed"
BUILD_DETAIL=""
SHOULD_PROBE=0
case "$PROBE" in
  force) SHOULD_PROBE=1 ;;
  never) SHOULD_PROBE=0 ;;
  auto)  [ "$SDK_SOURCE" = "distro package" ] && [ ! -s "$VERDICT_FILE" ] && SHOULD_PROBE=1 ;;
         # a generic failure is never cached, so `auto` retries it next run
esac

if [ "$SHOULD_PROBE" = "1" ]; then
  say "checking whether this SDK can build the repo (about a minute, once per container)"
  PROBE_LOG="$(mktemp)"
  if dotnet build src/Humans.Analyzers/Humans.Analyzers.csproj -v quiet >"$PROBE_LOG" 2>&1 \
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
    # Deliberately not "NO": the memory atom and section-doctor's Phase 0 both
    # treat NO as "this container cannot compile, do not touch C#". A compile
    # error in the checked-out branch is a different thing entirely, and one a
    # run may well be able to fix.
    BUILD_STATUS="unknown — probe failed"
    BUILD_DETAIL="the probe build failed for a reason other than CS9057 — most likely
               the branch that is checked out, not the toolchain. The SDK looks
               usable. Not cached; the next run re-checks. Log at $PROBE_LOG"
  fi
  # Only an environment-level answer is worth remembering. A generic build
  # failure usually says something about the branch that happens to be checked
  # out, and caching it would make every later run in this container docs-only
  # over a compile error that a different worktree does not have.
  case "$BUILD_STATUS" in
    yes|NO\ —\ CS9057)
      mkdir -p "$CACHE_DIR"
      printf 'full build: %s\n' "$BUILD_STATUS" >"$VERDICT_FILE"
      ;;
  esac
elif [ -s "$VERDICT_FILE" ]; then
  BUILD_STATUS="$(sed -n 's/^full build: //p' "$VERDICT_FILE" | head -1)"
  BUILD_DETAIL="cached from an earlier run in this container; --probe to re-check"
elif [ "$SDK_SOURCE" = "already present" ]; then
  BUILD_STATUS="not checked"
  BUILD_DETAIL="this script did not install the SDK and has not tried to build with
               it. Normal on a local checkout. If a build later fails CS9057,
               that is this — run --probe to confirm."
else
  BUILD_STATUS="not checked"
  BUILD_DETAIL="installed from the ${SDK_SOURCE}, which carries every feature band,
               so a build is expected to work. --probe to confirm."
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

# The caller's next command is a new shell. Say plainly whether what we set up
# will still be there, because a happy summary followed by "dotnet: not found"
# is the worst outcome this script can produce.
UNREACHABLE=""
# Presence is not enough: the caller may already have a `dotnet` that cannot
# satisfy global.json, in which case finding one on their PATH proves nothing.
# Run the real resolution under their PATH.
( export PATH="$CALLER_PATH"; command -v dotnet >/dev/null 2>&1 && dotnet --version >/dev/null 2>&1 ) \
  || UNREACHABLE="a usable dotnet"
if [ "$TOOLS" = "1" ] && [ "$REFORGE_STATUS" != "FAILED" ] \
   && ! PATH="$CALLER_PATH" command -v reforge >/dev/null 2>&1; then
  UNREACHABLE="${UNREACHABLE:+$UNREACHABLE and }reforge"
fi
if [ -n "$UNREACHABLE" ]; then
  cat <<EOF

  WARNING  ${UNREACHABLE} live under \$HOME and could not be linked onto PATH.
           This shell can see them; the next command cannot. Prefix later
           commands with:
               export PATH="\$HOME/.dotnet:\$HOME/.dotnet/tools:\$PATH"
EOF
fi
