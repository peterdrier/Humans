#!/usr/bin/env bash
# Installed outside the disposable checkout; selects one runner revision per run.
set -euo pipefail
runner_home="${DEBT_RUNNER_HOME:-$HOME/.humans-debt-runner}"
mkdir -p "$runner_home/releases"
exec {bootstrap_lock}>"$runner_home/.bootstrap.lock"
if ! flock -n "$bootstrap_lock"; then
  echo 'Another scheduled debt run holds the bootstrap lock; skipping.'
  exit 0
fi
trap 'rc=$?; if (( rc != 0 )); then echo "Runner bootstrap failed (exit $rc); no stale runner fallback." >&2; fi' EXIT
repo_url="$(cd "$runner_home/clone" && git remote get-url origin)"
source_dir="$runner_home/runner-source.git"
if [[ ! -d "$source_dir" ]]; then
  git init --bare --quiet "$source_dir"
fi
cd "$source_dir"
git fetch --quiet "$repo_url" '+refs/heads/main:refs/heads/runner-main'
revision="$(git rev-parse --verify 'refs/heads/runner-main^{commit}')"
release="$runner_home/releases/$revision"
if [[ ! -d "$release" ]]; then
  staging="$(mktemp -d "$runner_home/releases/.staging-XXXXXXXX")"
  git archive "$revision" .codex/cron .codex/prompts | tar -x -C "$staging"
  test -f "$staging/.codex/cron/run-daily-debt.sh"
  ln -s "$runner_home/clone/.codex/cron/debt-runner.env" "$staging/.codex/cron/debt-runner.env"
  mv "$staging" "$release"
fi
echo "Starting debt runner revision=$revision from origin/main release=$release"
exec bash "$release/.codex/cron/run-daily-debt.sh"
