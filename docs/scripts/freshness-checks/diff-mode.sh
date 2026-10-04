#!/bin/bash
# Test the freshness sweep's diff-mode logic in isolation.
#
# Exercises Phase 3 (catalog/marker discovery) and Phase 4 (trigger
# glob matching against a synthetic diff). Does NOT spin up a real
# worktree or invoke subagents — that's an end-to-end integration
# test for a different layer.
#
# Usage: bash docs/scripts/freshness-checks/diff-mode.sh
#
# Asserts:
#   1. freshness-catalog.yml parses cleanly (yaml + structural fields).
#   2. Every mechanical entry's trigger globs match at least one file.
#   3. Editorial walks find the expected docs (sections / features / guide).
#   4. Every marked editorial doc has well-formed marker syntax.
#   5. A synthetic diff containing src/Sections/Humans.Teams/Controllers/TeamController.cs
#      marks at least one mechanical entry dirty AND at least one
#      editorial doc dirty (the Team-related docs).
#   6. A synthetic diff containing only docs/* changes marks ZERO
#      entries dirty (docs aren't src/, so no triggers should fire).
#   7. Every freshness:triggers glob in every editorial doc resolves to at
#      least one real file.
#   8. trigger_is_dead (the function test 7 relies on) is proven in BOTH
#      directions with synthetic input, not just whatever today's real docs
#      happen to contain (nobodies-collective/Humans#1021).
#   9. Trigger repairs report failures truthfully and continue to later docs.
#  10. Authorization inventory rejects failed handler scans.
#  11. Suppression inventory distinguishes empty inputs from failed scans.
#
# Test 7 exists because a dead trigger glob is SILENT: it makes a doc look
# *clean* rather than *unchecked*, so the doc drops out of the sweep's dirty
# list entirely and nobody notices. Five consecutive sweeps found large dead-glob
# batches; the 2026-08-13 sweep found three guide docs that had stopped firing
# months earlier, one with all 9 of its globs dead. Tests 1-6 all passed
# throughout — none of them could see it.
#
# The editorial doc set spans BOTH docs/ and src/Sections/*/Docs/. Sections that
# have gone G5 carry their invariants doc inside their own project, and that is
# now where most section docs live — a walk that only covers docs/ misses them.

set -euo pipefail

CATALOG="docs/architecture/freshness-catalog.yml"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PASS=0
FAIL=0

if [ ! -f "$CATALOG" ]; then
  echo "FAIL: $CATALOG not found. Run from repo root."
  exit 1
fi

# editorial_docs, editorial_entries_unresolved, doc_trigger_lines and
# trigger_is_dead live in lib-editorial-docs.sh, shared with
# verify-triggers.sh (nobodies-collective/Humans#1021) so this script's
# definition of "the editorial doc set" and "a dead trigger" cannot drift
# from the one the sweep itself uses to repair them.
# shellcheck source=./lib-editorial-docs.sh
source "$SCRIPT_DIR/lib-editorial-docs.sh"

# ─── Test 1: Catalog parses (structural smoke) ────────────────────────
# grep -c already prints zero for no matches; status 1 is valid empty input,
# while read failures (including partial output) must not become a clean count.
if ! N_MECHANICAL=$(grep -cE '^\s+- id:\s+' "$CATALOG" || [ "$?" -eq 1 ]); then
  echo "FAIL [test 1]: could not count mechanical catalog entries"
  exit 1
fi
# NB: the range must start AFTER the editorial_trees: line, because that line
# itself matches /^[a-z]/ and would close the range immediately — which is why
# this reported "0 editorial trees" while the catalog listed ten.
N_TREES=$(awk '/^editorial_trees:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" | { grep -cE '^\s+- ' || true; })
N_IGNORE=$(awk '/^ignore:/{f=1;next} f' "$CATALOG" | { grep -cE '^\s+- ' || true; })

if [ "$N_MECHANICAL" -lt 5 ]; then
  echo "FAIL [test 1]: only $N_MECHANICAL mechanical entries (expected >= 5)"
  FAIL=$((FAIL+1))
else
  echo "PASS [test 1]: catalog has $N_MECHANICAL mechanical, $N_TREES editorial trees, $N_IGNORE ignore patterns"
  PASS=$((PASS+1))
fi

# ─── Test 2: Mechanical entry trigger globs match real files ──────────
shopt -s globstar nullglob
total=0; bad=0
in_mechanical=false
in_triggers=false
while IFS= read -r line; do
  if [[ "$line" =~ ^mechanical: ]]; then in_mechanical=true; continue; fi
  if [[ "$line" =~ ^[a-z_]+: ]] && [[ ! "$line" =~ ^\s ]]; then in_mechanical=false; continue; fi
  if ! $in_mechanical; then continue; fi
  if [[ "$line" =~ ^[[:space:]]+triggers: ]]; then in_triggers=true; continue; fi
  if [[ "$line" =~ ^[[:space:]]+[a-z_]+: ]] && ! [[ "$line" =~ ^[[:space:]]+- ]]; then in_triggers=false; continue; fi
  if $in_triggers && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+\"(.+)\"[[:space:]]*$ ]]; then
    glob="${BASH_REMATCH[1]}"
    total=$((total+1))
    # trigger_is_dead (lib-editorial-docs.sh) branches on wildcard vs literal —
    # mechanical entries mix both (e.g. "Directory.Packages.props" alongside
    # "**/*.csproj"), and a plain `matches=( $glob )` word-splits a literal to
    # itself regardless of whether the file exists, same blind spot test 7 had.
    if trigger_is_dead "$glob"; then
      echo "  [test 2]: ZERO MATCH glob: $glob"
      bad=$((bad+1))
    fi
  fi
done < "$CATALOG"

if [ "$bad" -eq 0 ]; then
  echo "PASS [test 2]: all $total mechanical-entry trigger globs match real files"
  PASS=$((PASS+1))
else
  echo "FAIL [test 2]: $bad of $total mechanical-entry trigger globs are stale"
  FAIL=$((FAIL+1))
fi

# ─── Test 3: Editorial walks find expected counts ─────────────────────
SEC=$(find docs/sections -name '*.md' -not -name 'SECTION-TEMPLATE.md' -not -name 'G5-SECTION-TEMPLATE.md' | wc -l)
FEAT=$(find docs/features -name '*.md' | wc -l)
GUIDE=$(find docs/guide -name '*.md' -not -name 'README.md' -not -name 'GettingStarted.md' -not -name 'Glossary.md' | wc -l)
INPROJ=$(find src/Sections -path '*/Docs/*.md' -not -path '*/Docs/20*.md' -not -path '*/obj/*' -not -path '*/bin/*' | wc -l)
# Catalog entries listed as single files rather than directories to walk.
SINGLES=$(awk '/^editorial_trees:/{f=1;next} f&&/^[a-z_]+:/{f=0} f' "$CATALOG" \
          | grep -E '^[[:space:]]+- ' | sed 's/^[[:space:]]*-[[:space:]]*//; s/[[:space:]]*$//' \
          | while IFS= read -r e; do if [ -f "$e" ]; then echo "$e"; fi; done | wc -l)
EDITORIAL_DOCS=$(editorial_docs)
TOTAL=$(printf '%s\n' "$EDITORIAL_DOCS" | sed '/^$/d' | wc -l)
UNRESOLVED=$(editorial_entries_unresolved)
N_UNRESOLVED=0
if [ -n "$UNRESOLVED" ]; then
  N_UNRESOLVED=$(printf '%s\n' "$UNRESOLVED" | sed '/^$/d' | wc -l)
  printf '%s\n' "$UNRESOLVED" | sed '/^$/d; s|^|  [test 3]: catalog editorial_trees entry resolves to nothing: |'
fi

if [ "$TOTAL" -lt 50 ] || [ "$INPROJ" -lt 1 ] || [ "$SINGLES" -lt 1 ] || [ "$N_UNRESOLVED" -gt 0 ]; then
  echo "FAIL [test 3]: editorial walk found $TOTAL docs ($INPROJ in-project, $SINGLES single-file, $N_UNRESOLVED dead catalog entries)"
  FAIL=$((FAIL+1))
else
  echo "PASS [test 3]: editorial walk: sections=$SEC features=$FEAT guide=$GUIDE in-project=$INPROJ single-file=$SINGLES = $TOTAL total, 0 dead catalog entries"
  PASS=$((PASS+1))
fi

# ─── Test 4: Marker syntax well-formedness on every editorial doc ─────
malformed=0
for f in $EDITORIAL_DOCS; do
  has_triggers=$(grep -c '<!-- freshness:triggers' "$f" || true)
  close_count=$(grep -cE '^-->' "$f" || true)
  if [ "$has_triggers" -gt 0 ] && [ "$close_count" -lt "$has_triggers" ]; then
    echo "  [test 4]: marker imbalance in $f (open=$has_triggers close=$close_count)"
    malformed=$((malformed+1))
  fi
done

if [ "$malformed" -eq 0 ]; then
  echo "PASS [test 4]: every marked editorial doc has matched open/close markers"
  PASS=$((PASS+1))
else
  echo "FAIL [test 4]: $malformed editorial docs have malformed markers"
  FAIL=$((FAIL+1))
fi

# ─── Test 5: Synthetic diff (TeamController.cs) marks expected dirty ──
SYNTHETIC="src/Sections/Humans.Teams/Controllers/TeamController.cs"
# A synthetic probe that does not exist is a test that silently proves nothing:
# it reports 0 dirty and reads as a real failure, or worse, gets ignored. Assert
# the probe itself first — this test pointed at the pre-G5 Humans.Web path for
# several sweeps after the file moved.
if [ ! -f "$SYNTHETIC" ]; then
  echo "FAIL [test 5]: synthetic probe path $SYNTHETIC does not exist — update it to a real file"
  FAIL=$((FAIL+1))
fi
mech_dirty=0
for entry in authorization-inventory dependency-graph; do
  in_block=false
  in_triggers=false
  while IFS= read -r line; do
    if [[ "$line" =~ ^[[:space:]]+-[[:space:]]+id:[[:space:]]+$entry$ ]]; then in_block=true; continue; fi
    if $in_block && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+id:[[:space:]] ]]; then break; fi
    if $in_block && [[ "$line" =~ ^[[:space:]]+triggers: ]]; then in_triggers=true; continue; fi
    if $in_block && $in_triggers && [[ "$line" =~ ^[[:space:]]+update: ]]; then in_triggers=false; continue; fi
    if $in_block && $in_triggers && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+\"(.+)\"[[:space:]]*$ ]]; then
      glob="${BASH_REMATCH[1]}"
      matches=( $glob )
      for m in "${matches[@]}"; do
        if [ "$m" = "$SYNTHETIC" ]; then
          mech_dirty=$((mech_dirty+1))
          break 2
        fi
      done
    fi
  done < "$CATALOG"
done

ed_dirty=0
for f in src/Sections/Humans.Teams/Docs/Teams.md src/Sections/Humans.Teams/Docs/features/Teams-feature.md docs/guide/Teams.md; do
  triggers=$(awk '/<!-- freshness:triggers/,/^-->/' "$f" 2>/dev/null | grep -E '^\s+src/' | sed 's/^\s*//;s/\s*$//')
  while IFS= read -r glob; do
    [ -z "$glob" ] && continue
    matches=( $glob )
    for m in "${matches[@]}"; do
      if [ "$m" = "$SYNTHETIC" ]; then
        ed_dirty=$((ed_dirty+1))
        break 2
      fi
    done
  done <<< "$triggers"
done

if [ "$mech_dirty" -ge 1 ] && [ "$ed_dirty" -ge 1 ]; then
  echo "PASS [test 5]: synthetic TeamController.cs change marks $mech_dirty mechanical + $ed_dirty editorial dirty"
  PASS=$((PASS+1))
else
  echo "FAIL [test 5]: synthetic TeamController.cs change should mark >=1 mechanical and >=1 editorial dirty (got $mech_dirty + $ed_dirty)"
  FAIL=$((FAIL+1))
fi

# ─── Test 6: docs-only diff marks ZERO entries dirty ──────────────────
DOC_ONLY="docs/freshness/last-report.md"
mech_dirty=0
in_mechanical=false
in_triggers=false
while IFS= read -r line; do
  if [[ "$line" =~ ^mechanical: ]]; then in_mechanical=true; continue; fi
  if [[ "$line" =~ ^[a-z_]+: ]] && [[ ! "$line" =~ ^\s ]]; then in_mechanical=false; continue; fi
  if ! $in_mechanical; then continue; fi
  if [[ "$line" =~ ^[[:space:]]+triggers: ]]; then in_triggers=true; continue; fi
  if [[ "$line" =~ ^[[:space:]]+[a-z_]+: ]] && ! [[ "$line" =~ ^[[:space:]]+- ]]; then in_triggers=false; continue; fi
  if $in_triggers && [[ "$line" =~ ^[[:space:]]+-[[:space:]]+\"(.+)\"[[:space:]]*$ ]]; then
    glob="${BASH_REMATCH[1]}"
    matches=( $glob )
    for m in "${matches[@]}"; do
      if [ "$m" = "$DOC_ONLY" ]; then mech_dirty=$((mech_dirty+1)); break; fi
    done
  fi
done < "$CATALOG"

if [ "$mech_dirty" -eq 0 ]; then
  echo "PASS [test 6]: docs-only diff ($DOC_ONLY) marks 0 mechanical entries dirty"
  PASS=$((PASS+1))
else
  echo "FAIL [test 6]: docs-only diff should mark 0 dirty (got $mech_dirty)"
  FAIL=$((FAIL+1))
fi

# ─── Test 7: Every editorial trigger glob resolves to a real file ─────
# The one check that can see a dead trigger. See the header note. Uses the
# shared doc_trigger_lines/trigger_is_dead from lib-editorial-docs.sh — the
# same functions verify-triggers.sh uses to repair these, so a fix to the
# detection logic can't land in one script and not the other.
dead_globs=0
dead_docs=0
checked_docs=0
for f in $EDITORIAL_DOCS; do
  # `doc_trigger_lines` already guards grep's no-marker status — see its
  # definition for why that matters under `set -o pipefail`.
  triggers=$(doc_trigger_lines "$f")
  if [ -z "$triggers" ]; then continue; fi
  checked_docs=$((checked_docs+1))
  doc_dead=0
  doc_total=0
  while IFS= read -r glob; do
    [ -z "$glob" ] && continue
    doc_total=$((doc_total+1))
    # Both branches must END on a successful command. Under `set -euo pipefail`
    # a trailing `cond && assign` returns non-zero whenever cond is false, which
    # aborts the whole script mid-test — the same way a bare trailing `if` once
    # killed everything after test 2. Use explicit if/else, never `&&`.
    if trigger_is_dead "$glob"; then
      echo "  [test 7]: DEAD trigger in $f -> $glob"
      dead_globs=$((dead_globs+1))
      doc_dead=$((doc_dead+1))
    fi
  done <<< "$triggers"
  if [ "$doc_dead" -gt 0 ]; then
    dead_docs=$((dead_docs+1))
    if [ "$doc_dead" -eq "$doc_total" ]; then
      echo "  [test 7]: ** $f is FULLY DEAD ($doc_dead/$doc_total) — it has stopped firing entirely **"
    fi
  fi
done

if [ "$dead_globs" -eq 0 ]; then
  echo "PASS [test 7]: all triggers across $checked_docs editorial docs resolve"
  PASS=$((PASS+1))
else
  echo "FAIL [test 7]: $dead_globs dead triggers across $dead_docs of $checked_docs docs"
  FAIL=$((FAIL+1))
fi

# ─── Test 8: Synthetic proof — trigger_is_dead sees BOTH directions ───────
# Test 7 only ever exercises trigger_is_dead against docs' CURRENT triggers —
# whatever happens to be alive or dead in the repo today. That proves nothing
# about the function itself: every dead trigger test 7 finds gets repaired
# and stops proving the "reports a real failure" direction tomorrow, and if
# every trigger in the repo happened to be alive, test 7 would never have
# exercised the dead-detection branch at all (nobodies-collective/Humans#1021
# — "a test that only ever sees passing input proves nothing"). This test is
# independent of doc content: it feeds trigger_is_dead synthetic real and
# fake paths directly, one pair per branch (wildcard, literal), and asserts
# each direction explicitly.
real_glob="docs/architecture/**"          # real dir — must NOT be reported dead
dead_glob="src/Humans.NoSuchSection.DoesNotExist/**"   # never existed — must be reported dead
real_literal="$CATALOG"                   # real file — must NOT be reported dead
dead_literal="docs/architecture/this-file-does-not-exist-1021.md"  # must be reported dead

t8_fail=""
if trigger_is_dead "$real_glob"; then t8_fail="$t8_fail glob-real-passed-as-dead"; fi
if ! trigger_is_dead "$dead_glob"; then t8_fail="$t8_fail glob-dead-passed-as-real"; fi
if trigger_is_dead "$real_literal"; then t8_fail="$t8_fail literal-real-passed-as-dead"; fi
if ! trigger_is_dead "$dead_literal"; then t8_fail="$t8_fail literal-dead-passed-as-real"; fi

if [ -z "$t8_fail" ]; then
  echo "PASS [test 8]: trigger_is_dead proves both directions (glob + literal, real + dead)"
  PASS=$((PASS+1))
else
  echo "FAIL [test 8]: trigger_is_dead direction check(s) broken:$t8_fail"
  FAIL=$((FAIL+1))
fi

echo ""
# ─── Test 9: Failed trigger repairs remain dirty ────────────────────────
if python3 - "$SCRIPT_DIR/verify-triggers.sh" <<'PYTEST'
from pathlib import Path
import tempfile, shutil, subprocess, os
import sys
script=Path(sys.argv[1]).resolve()
def case(mode, failure=None, wildcard=False):
    with tempfile.TemporaryDirectory() as tmp:
        p=Path(tmp)
        (p/'docs/architecture').mkdir(parents=True)
        (p/'src/new').mkdir(parents=True)
        (p/'src/new/Thing.cs').write_text('target')
        (p/'src/new/Other.cs').write_text('target')
        (p/'docs/architecture/freshness-catalog.yml').write_text('editorial_trees:\n  - docs/one.md\n  - docs/two.md\nignore:\n  - ignored/**\n')
        original='<!-- freshness:triggers\n  src/old/Thing.cs\n-->\n'
        if wildcard:
            original = original.replace('Thing.cs', 'new/**/Thing.cs')
        (p/'docs/one.md').write_text(original)
        (p/'docs/two.md').write_text(original.replace('Thing','Other'))
        (p/'docs/empty.md').write_text('No trigger marker.\n')
        if failure == 'empty-ignore':
            (p/'docs/architecture/freshness-catalog.yml').write_text('editorial_trees:\n  - docs/\nignore:\n')
        if failure == 'walk-find':
            (p/'docs/architecture/freshness-catalog.yml').write_text('editorial_trees:\n  - docs/\nignore:\n  - ignored/**\n')
        if failure == 'dead-suffix':
            original = original.replace('Thing.cs', 'new/**/Missing.cs')
            (p/'docs/one.md').write_text(original)
        env=os.environ.copy()
        if failure and failure not in ('empty-ignore', 'dead-suffix'):
            (p/'bin').mkdir()
            command={'walk-find':'find', 'ignore-awk':'awk', 'read-awk':'awk'}.get(failure, failure)
            real=shutil.which(command)
            # Fail only first doc, or return a plausible partial target with error.
            code=(f'#!/bin/bash\nif [[ "$*" == *one.md* ]]; then exit 42; fi\nexec {real} "$@"\n' if failure=='mv' else
                  f'#!/bin/bash\nif [[ "$*" == *"-v old="*Thing* ]]; then printf partial; exit 42; fi\nexec {real} "$@"\n' if failure=='awk' else
                  f'#!/bin/bash\n{real} "$@"\nexit 42\n')
            if failure == 'walk-find':
                code=f'#!/bin/bash\n{real} "$@"\nif [[ "$1" == docs ]]; then exit 42; fi\n'
            elif failure == 'ignore-awk':
                code=f'#!/bin/bash\nif [[ "$1" == *"/^ignore:/"* ]]; then printf "ignored/**\\n"; exit 42; fi\nexec {real} "$@"\n'
            elif failure == 'read-awk':
                code=f'#!/bin/bash\nif [[ "$*" == *one.md* && "$1" != -v ]]; then printf "src/old/Thing.cs\\n"; exit 42; fi\nexec {real} "$@"\n'
            tool=p/'bin'/command;tool.write_text(code);tool.chmod(0o755)
            env['PATH']=str(p/'bin')+':'+env['PATH']
        result=subprocess.run(['bash',str(script)]+(['--check'] if mode=='check' else []),cwd=p,env=env,text=True,capture_output=True)
        if failure in ('walk-find', 'ignore-awk'):
            assert result.returncode != 0, result
            assert 'ERROR' in result.stdout + result.stderr, result
            assert 'SUMMARY' not in result.stdout, result.stdout
            assert (p/'docs/one.md').read_text() == original
            assert (p/'docs/two.md').read_text() == original.replace('Thing', 'Other')
            print('PASS', mode, failure)
            return
        assert result.returncode==0,result
        if failure and failure != 'empty-ignore':
            assert 'UNRESOLVED docs/one.md' in result.stdout,result.stdout
            assert 'FORCE-DIRTY docs/one.md' in result.stdout,result.stdout
            assert (p/'docs/one.md').read_text()==original
            assert 'REPAIRED docs/one.md' not in result.stdout,result.stdout
            if failure!='find':
                assert 'REPAIRED docs/two.md' in result.stdout,result.stdout
                assert 'repaired=1 unresolved=1 docs_forced_dirty=1' in result.stdout,result.stdout
            assert not list((p/'docs').glob('*.tmp'))
        else:
            assert 'repaired=2 unresolved=0 docs_forced_dirty=0' in result.stdout,result.stdout
            assert (p/'docs/one.md').read_text()==(original if mode=='check' else original.replace('old/new' if wildcard else 'old', 'new'))
        print('PASS',mode,failure or ('wildcard' if wildcard else 'normal'))
for args in [('repair',None),('check',None),('repair','mv'),('repair','awk'),('repair','find'),('repair','walk-find'),('repair','ignore-awk'),('repair','read-awk'),('repair','empty-ignore'),('repair','dead-suffix'),('check','dead-suffix')]:case(*args)
case('repair', wildcard=True)
case('check', wildcard=True)
PYTEST
then
  echo "PASS [test 9]: repair/preview and empty markers/lists work; producer failures cannot pass clean"
  PASS=$((PASS+1))
else
  echo "FAIL [test 9]: trigger repair failure handling"
  FAIL=$((FAIL+1))
fi

# A failed handler scan must not turn into a zero-handler authorization PASS,
# even when the producer returned some usable-looking output before failing.
if python3 - "$SCRIPT_DIR/authorization-inventory.sh" <<'PYTEST'
import os, pathlib, shutil, subprocess, sys, tempfile
script = pathlib.Path(sys.argv[1]).resolve()
real_grep = shutil.which('grep')
for partial in (False, True):
    with tempfile.TemporaryDirectory() as directory:
        probe = pathlib.Path(directory) / 'grep'
        probe.write_text(
            '#!/bin/bash\nif [[ "$1" == -rlE ]]; then\n'
            + (f'{real_grep} "$@"\n' if partial else '')
            + 'exit 42\nfi\n' + f'exec {real_grep} "$@"\n')
        probe.chmod(0o755)
        env = os.environ.copy()
        env['PATH'] = directory + ':' + env['PATH']
        result = subprocess.run(['bash', str(script)], env=env, capture_output=True, text=True)
        assert result.returncode != 0, result.stdout
        assert 'could not enumerate authorization handlers' in result.stdout, result
        assert 'PASS [authorization-inventory]' not in result.stdout, result.stdout
PYTEST
then
  echo "PASS [test 10]: authorization inventory rejects failed and partial handler scans"
  PASS=$((PASS+1))
else
  echo "FAIL [test 10]: authorization handler scan failure handling"
  FAIL=$((FAIL+1))
fi

# A missing props file or failed NoWarn producer cannot reduce the population
# to zero and pass. Real empty NoWarn input is still valid.
if python3 - "$SCRIPT_DIR/code-analysis-suppressions.sh" <<'PYTEST'
import os, pathlib, shutil, subprocess, sys, tempfile
script = pathlib.Path(sys.argv[1]).resolve()
real_grep = shutil.which('grep')
for mode in ('empty', 'failed', 'partial', 'missing-root', 'missing-tests'):
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        (root / 'docs/architecture').mkdir(parents=True)
        (root / 'tests').mkdir()
        (root / 'docs/architecture/code-analysis.md').write_text(
            '<!-- freshness:auto id="suppressions" -->\nCS0618\n<!-- /freshness:auto -->\n')
        props = '<Project />\n' if mode == 'empty' else '<NoWarn>CS0618</NoWarn>\n'
        if mode != 'missing-root':
            (root / 'Directory.Build.props').write_text(props)
        if mode != 'missing-tests':
            (root / 'tests/Directory.Build.props').write_text(props)
        env = os.environ.copy()
        if mode in ('failed', 'partial'):
            probe = root / 'grep'
            probe.write_text(
                '#!/bin/bash\nif [[ "$1" == -oE ]]; then\n'
                + (f'{real_grep} "$@"\n' if mode == 'partial' else '')
                + 'exit 42\nfi\n' + f'exec {real_grep} "$@"\n')
            probe.chmod(0o755)
            env['PATH'] = directory + ':' + env['PATH']
        result = subprocess.run(['bash', str(script)], cwd=root, env=env, capture_output=True, text=True)
        if mode == 'empty':
            assert result.returncode == 0, result
            assert 'all 0 suppression codes' in result.stdout, result.stdout
        else:
            assert result.returncode != 0, result.stdout
            assert 'could not enumerate analyzer suppressions' in result.stdout, result
            assert 'PASS [code-analysis-suppressions]' not in result.stdout, result.stdout
PYTEST
then
  echo "PASS [test 11]: suppression inventory accepts empty input and rejects missing/failed/partial scans"
  PASS=$((PASS+1))
else
  echo "FAIL [test 11]: analyzer suppression scan failure handling"
  FAIL=$((FAIL+1))
fi

# Multiword package aliases must match as phrases, not unrelated individual
# words elsewhere on the About page.
if python3 - "$SCRIPT_DIR/about-page-packages.sh" <<'PYTEST'
import pathlib, subprocess, sys, tempfile
script = pathlib.Path(sys.argv[1]).resolve()
for package, alias, fragment in (
    ('Microsoft.EntityFrameworkCore', 'entity framework core', 'core'),
    ('Microsoft.AspNetCore.Authentication.Google', 'google authentication', 'google'),
    ('Google.Apis.Drive.v3', 'drive api', 'api'),
):
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        about = root / 'src/Humans.Web/Views/About/Index.cshtml'
        about.parent.mkdir(parents=True)
        (root / 'Directory.Packages.props').write_text(f'<PackageVersion Include="{package}" Version="1"/>')
        (root / 'src/Humans.Web/Test.csproj').write_text(f'<PackageReference Include="{package}"/>')
        for text, expected in ((package, 0), (alias, 0), (fragment, 1), ('Unrelated prose', 1)):
            about.write_text(text)
            result = subprocess.run(['bash', str(script)], cwd=root, capture_output=True, text=True)
            assert result.returncode == expected, (package, text, result)
            assert ('PASS [about-page-packages]' in result.stdout) == (expected == 0), result.stdout
PYTEST
then
  echo "PASS [test 12]: package inventory matches complete aliases and rejects isolated words"
  PASS=$((PASS+1))
else
  echo "FAIL [test 12]: package alias matching"
  FAIL=$((FAIL+1))
fi

# History/statistics inputs must be read successfully, including when a failed
# producer emitted enough valid-looking rows to otherwise pass the check.
if python3 - "$SCRIPT_DIR/dev-stats.sh" "$SCRIPT_DIR/reforge-history.sh" <<'PYTEST'
import os, pathlib, shutil, subprocess, sys, tempfile
for script_path in sys.argv[1:]:
    script = pathlib.Path(script_path).resolve()
    is_stats = script.name == 'dev-stats.sh'
    for mode in ('valid', 'empty', 'partial', 'failed'):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'docs').mkdir()
            probes = root / 'probes'
            probes.mkdir()
            git = probes / 'git'
            date_output = '' if mode == 'empty' and not is_stats else 'echo 2026-10-01; '
            git.write_text('#!/bin/bash\nif [[ "$1" == rev-parse ]]; then exit 0; fi\n'
                           + 'if [[ "$1" == log ]]; then ' + date_output + 'exit 0; fi\nexit 2\n')
            git.chmod(0o755)
            if is_stats:
                row = '| 2026-10-01 | ' + ' | '.join(['1'] * 19) + ' |\n'
                (root / 'docs/development-stats.md').write_text('No rows\n' if mode == 'empty' else row)
                command, match = 'grep', '[[ "$1" == -E ]]'
            else:
                row = 'commit_date,a,b,c,d\n' + ('' if mode == 'empty' else '2026-10-01,1,2,3,4\n')
                (root / 'docs/reforge-history.csv').write_text(row)
                command, match = 'tail', '[[ "$1" == -n && "$2" == +2 ]]'
            if mode in ('failed', 'partial'):
                real_command = shutil.which(command)
                probe = probes / command
                probe.write_text('#!/bin/bash\nif ' + match + '; then\n'
                                 + (f'{real_command} "$@"\n' if mode == 'partial' else '')
                                 + 'exit 42\nfi\n' + f'exec {real_command} "$@"\n')
                probe.chmod(0o755)
            env = os.environ.copy()
            env['PATH'] = str(probes) + ':' + env['PATH']
            result = subprocess.run(['bash', str(script)], cwd=root, env=env, capture_output=True, text=True)
            if mode == 'valid' or (mode == 'empty' and not is_stats):
                assert result.returncode == 0 and 'PASS [' in result.stdout, result
            else:
                assert result.returncode != 0 and 'PASS [' not in result.stdout, result
                if mode in ('failed', 'partial'):
                    assert 'could not read' in result.stdout, result
PYTEST
then
  echo "PASS [test 13]: statistics and history verifiers reject failed and partial input scans"
  PASS=$((PASS+1))
else
  echo "FAIL [test 13]: statistics/history input failure handling"
  FAIL=$((FAIL+1))
fi

# Service discovery follows arbitrary inheritance depth, including diamonds and
# cycles, without counting unrelated interfaces as application services.
if python3 - "$SCRIPT_DIR/lib-service-classes.sh" <<'PYTEST'
import pathlib, subprocess, sys, tempfile
helper = pathlib.Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory() as directory:
    root = pathlib.Path(directory)
    services = root / 'src/Sections/Humans.Example/Services'
    services.mkdir(parents=True)
    (root / 'src/Humans.Web/Services').mkdir(parents=True)
    declarations = [
        f'public interface ILevel{i} : ' + ('IApplicationService' if i == 0 else f'ILevel{i - 1}') + ' {}'
        for i in range(8)
    ]
    declarations += [
        'public interface IDiamond : ILevel2, ILevel7 {}',
        'public interface ICycleA : ICycleB, IDiamond {}',
        'public interface ICycleB : ICycleA {}',
        'public interface IUnrelatedA : IUnrelatedB {}',
        'public interface IUnrelatedB : IUnrelatedA {}',
        'internal class DeepService : ILevel7 {}',
        'internal class DiamondService : IDiamond {}',
        'internal class CycleService : ICycleB {}',
        'internal class UnrelatedService : IUnrelatedA {}',
    ]
    (services / 'Example.cs').write_text('\n'.join(declarations))
    result = subprocess.run(
        ['bash', '-c', 'set -euo pipefail; source "$1"; service_classes', 'fixture', str(helper)],
        cwd=root, capture_output=True, text=True, timeout=10)
    assert result.returncode == 0, result
    names = {line.split('|')[1].split(',')[0] for line in result.stdout.splitlines()}
    assert names == {'DeepService', 'Diamond', 'CycleB'}, result.stdout
PYTEST
then
  echo "PASS [test 14]: service discovery follows deep inheritance to a fixed point"
  PASS=$((PASS+1))
else
  echo "FAIL [test 14]: service inheritance discovery"
  FAIL=$((FAIL+1))
fi

# Exercise this script's actual catalog smoke-check prefix without recursively
# running its remaining regression checks in each synthetic repository.
if python3 - "$0" <<'PYTEST'
import os, pathlib, subprocess, sys, tempfile
script = pathlib.Path(sys.argv[1]).resolve()
source = script.read_text().split('# ─── Test 2:', 1)[0]
for mode in ('empty', 'five', 'failed', 'partial'):
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        catalog = root / 'docs/architecture/freshness-catalog.yml'
        catalog.parent.mkdir(parents=True)
        entries = '' if mode == 'empty' else ''.join(f'  - id: fixture-{i}\n' for i in range(5))
        catalog.write_text('mechanical:\n' + entries + 'editorial_trees:\nignore:\n')
        env = os.environ.copy()
        if mode in ('failed', 'partial'):
            probes = root / 'probes'
            probes.mkdir()
            for command in ('grep', 'awk'):
                probe = probes / command
                probe.write_text('#!/bin/bash\n' + ('echo 5\n' if mode == 'partial' else '') + 'exit 42\n')
                probe.chmod(0o755)
            env['PATH'] = str(probes) + ':' + env['PATH']
        result = subprocess.run(['bash', '-c', source, str(script)],
                                cwd=root, env=env, capture_output=True, text=True, timeout=10)
        if mode == 'five':
            assert result.returncode == 0 and 'PASS [test 1]: catalog has 5 mechanical' in result.stdout, (result.returncode, result.stdout, result.stderr)
        else:
            assert 'FAIL [test 1]:' in result.stdout and 'PASS [test 1]:' not in result.stdout, (result.returncode, result.stdout, result.stderr)
            if mode == 'empty':
                assert 'only 0 mechanical entries' in result.stdout, (result.returncode, result.stdout, result.stderr)
            else:
                assert result.returncode != 0 and 'could not count' in result.stdout, (result.returncode, result.stdout, result.stderr)
        assert 'integer expression expected' not in result.stderr, (result.returncode, result.stdout, result.stderr)
PYTEST
then
  echo "PASS [test 15]: catalog counts reject zero entries and failed/partial reads"
  PASS=$((PASS+1))
else
  echo "FAIL [test 15]: catalog count failure handling"
  FAIL=$((FAIL+1))
fi

# Empty inventories are valid; failed producers must still report failure.
if python3 - "$SCRIPT_DIR/authorization-inventory.sh" "$SCRIPT_DIR/guid-reservations.sh" <<'PYTEST'
import os, pathlib, shutil, subprocess, sys, tempfile
for script_path in sys.argv[1:]:
    script = pathlib.Path(script_path).resolve()
    name = script.stem
    for failure in (None, 'grep', 'awk'):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'docs').mkdir()
            if name == 'authorization-inventory':
                for owner in ('src/Humans.Web', 'src/Sections/Humans.Example'):
                    (root / owner / 'Controllers').mkdir(parents=True)
                    (root / owner / 'Controllers/ExampleController.cs').write_text('class ExampleController {}')
                (root / 'src/Sections/Humans.Example/Docs').mkdir()
                (root / 'docs/authorization-inventory.md').write_text('ExampleController')
                (root / 'src/Sections/Humans.Example/Docs/authorization.md').write_text('ExampleController')
                # This verifier uses awk only to total the document row count.
                if failure == 'awk':
                    tool_pattern = ''
                else:
                    tool_pattern = '*"-hE"*'
            else:
                (root / 'src/Humans.Base/Constants').mkdir(parents=True)
                (root / 'src/Sections/Humans.Example/Data').mkdir(parents=True)
                (root / 'docs/guid-reservations.md').write_text('## Current Reservations\n| `0000` | Sentinel |\n')
                tool_pattern = '*"-rEho"*' if failure == 'grep' else ''
            env = os.environ.copy()
            if failure:
                (root / 'bin').mkdir()
                real = shutil.which(failure)
                condition = f'[[ "$*" == {tool_pattern} ]]' if tool_pattern else 'true'
                tool = root / 'bin' / failure
                tool.write_text(f'#!/bin/bash\nif {condition}; then printf partial; exit 42; fi\nexec {real} "$@"\n')
                tool.chmod(0o755)
                env['PATH'] = str(root / 'bin') + ':' + env['PATH']
            result = subprocess.run(['bash', str(script)], cwd=root, env=env, text=True, capture_output=True)
            if failure:
                assert result.returncode != 0 and f'FAIL [{name}]' in result.stdout, result
                assert f'PASS [{name}]' not in result.stdout, result.stdout
            else:
                assert result.returncode == 0 and f'PASS [{name}]' in result.stdout, result
PYTEST
then
  echo "PASS [test 16]: empty authorization/GUID inventories pass and failed producers report failure"
  PASS=$((PASS+1))
else
  echo "FAIL [test 16]: authorization/GUID inventory input handling"
  FAIL=$((FAIL+1))
fi

# A changed Reforge snapshot schema must never be appended below an old CSV
# header, or mixed with earlier snapshots in a full rebuild. Git/Reforge are
# stubbed: the fixture creates ordinary temporary folders, never worktrees.
if python3 - <<'PYTEST'
import os, pathlib, subprocess, tempfile
script = pathlib.Path('docs/scripts/generate-reforge-history.sh').resolve()
for full, mode in ((False, 'same'), (False, 'changed'), (True, 'changed'), (True, 'mixed')):
    with tempfile.TemporaryDirectory() as directory:
        root = pathlib.Path(directory)
        (root / 'docs').mkdir()
        output = root / 'docs/reforge-history.csv'
        original = 'commit_date,commit,metric\n2026-01-01,old,1\n'
        output.write_text(original)
        tools = root / 'bin'
        tools.mkdir()
        git = tools / 'git'
        git.write_text("""#!/bin/bash
case "$1" in
rev-parse) echo old;;
log) printf '2026-01-02 first\n2026-01-03 second\n';;
worktree)
  if [ "$2" = add ]; then mkdir -p "$5"; fi;;
-C)
  if [ "$3" = checkout ] && [ "$4" = --quiet ] && [ "$5" != HEAD ]; then
    printf '%s' "$5" > "$2/selected"
  fi;;
*) exit 42;;
esac
""")
        reforge = tools / 'reforge'
        reforge.write_text("""#!/bin/bash
commit=$(cat selected)
header=metric
if [ "$SCHEMA_MODE" = changed ] || { [ "$SCHEMA_MODE" = mixed ] && [ "$commit" = second ]; }; then header=other_metric; fi
if [ "$commit" = first ]; then day=2026-01-02; else day=2026-01-03; fi
printf 'commit_date,commit,%s\n%s,%s,2\n' "$header" "$day" "$commit" > "$5"
""")
        git.chmod(0o755)
        reforge.chmod(0o755)
        env = {**os.environ, 'PATH': f'{tools}:{os.environ["PATH"]}', 'SCHEMA_MODE': mode}
        result = subprocess.run(['bash', str(script), *(['--full'] if full else [])],
                                cwd=root, env=env, text=True, capture_output=True)
        if mode == 'mixed' or (not full and mode == 'changed'):
            assert result.returncode != 0, result
            assert output.read_text() == original, output.read_text()
            assert 'schema' in result.stderr.lower(), result.stderr
        else:
            assert result.returncode == 0, result
            rows = output.read_text().splitlines()
            expected = 'other_metric' if mode == 'changed' else 'metric'
            assert rows[0] == f'commit_date,commit,{expected}', rows
            assert len(rows) == (3 if full else 4), rows
PYTEST
then
  echo "PASS [test 17]: history generator preserves CSV on snapshot schema mismatch"
  PASS=$((PASS+1))
else
  echo "FAIL [test 17]: history generator snapshot schema validation"
  FAIL=$((FAIL+1))
fi

echo ""
echo "═══ Summary ═══"
echo "Passed: $PASS"
echo "Failed: $FAIL"
exit $FAIL
