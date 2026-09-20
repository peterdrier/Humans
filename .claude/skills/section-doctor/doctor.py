#!/usr/bin/env python3
"""Shell mechanics of a section-doctor run, in one place (SKILL.md tooling table).

    doctor.py rundir [--ts TS]          print the run's scratch dir (created); TS from the branch by default
    doctor.py mark <phase-id> <label…>  append a timestamped phase-log line for cost-report.py
    doctor.py push                      origin gate, then push the current branch
    doctor.py commit -F FILE | -m MSG   prose gate over the staged diff (git add first), logged to gates.log, then git commit
    doctor.py prose-gate [--base REF]   count-in-prose gate over the staged diff (or REF..HEAD)
    doctor.py dispatch-log <thread> <model> [agent-type]   record a subagent dispatch
    doctor.py resolve-check <sha>       the commit exists and is on origin/<current branch>
    doctor.py inventory <Section>       every tracked path of the section (Phase 3a), generated ones tagged
    doctor.py check-run-file <path> --section <Section>   the run file carries every required block and a disposition per inventory path
    doctor.py runfile <Section> [--invocation TEXT] [--budget B]   create the run file, or refresh its coverage and thread tables
    doctor.py comments <Section>    every comment in the section's code, path:line: text (the Comments thread's input)
    doctor.py history <Section>     lines narrating a prior state across docs and comments (the History thread's candidates)
    doctor.py trace <doc>… [--section X]   the trace gate: every backticked name, route, path and file:line resolved against the tree
    doctor.py blast <symbol>…       repo-wide word-bounded git grep per symbol (blast radius before a strike names one)
    doctor.py review-pack <Section> <what> [--finding TEXT]   capture the uncommitted diff, its blast grep and file heads for the reviewer; name the reviewer agent

Every subcommand derives the run's identity from the branch (`section-doctor/<TS>`), so nothing
depends on shell state surviving between tool calls. Non-zero exit means "stop and look".
"""
import argparse
import glob
import os
import re
import signal
import subprocess
import sys
from datetime import datetime, timezone

ORIGIN_RE = re.compile(r"github\.com[:/]peterdrier/Humans(\.git)?$")
BRANCH_RE = re.compile(r"^section-doctor/(.+)$")
# "one" is a pronoun ("so that one is logged") far more often than a count, and a count of one
# never takes a plural — so it is not a numeral here.
WORDS = {w: i + 2 for i, w in enumerate(
    "two three four five six seven eight nine ten eleven twelve".split())}
NUM = r"(?:[0-9]+|" + "|".join(WORDS) + ")"
# Must-fix: a count with a structural tell — it names the rows under it, or it is a total.
STRUCTURAL_RE = re.compile(
    r"(?:"
    r"\(\s*[0-9]+\s*\)"                               # "Routes (3)"
    r"|\b(?:total|count|n)\s*[:=]\s*[0-9]+\b"          # "Total: 3", "count = 3"
    r"|\|\s*(?:total|count)\s*\|\s*[0-9]+\s*\|"      # markdown total row
    r")",
    re.IGNORECASE,
)
HEADING_RE = re.compile(r"^#+\s")
# Advisory: any numeral followed by a plural within three words — most prose about code.
LOOSE_RE = re.compile(rf"\b({NUM})\s+(?:[a-z-]+\s+){{0,2}}[a-z-]+s\b", re.IGNORECASE)
TABLE_ROW_RE = re.compile(r"^\|(?!\s*-)")     # a table row that is not the |---| separator
BULLET_RE = re.compile(r"^\s*(?:[-*+]|[0-9]+[.)])\s")
PROSE_SUFFIXES = (".md", ".yml", ".yaml", ".txt", ".cshtml", ".resx")
COMMENT_RE = re.compile(r"^\s*(?://|/\*|\*)")
FENCE_RE = re.compile(r"^\s*(?:```|~~~)")


def git(*args, check=True):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=check).stdout.strip()


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def branch():
    return git("rev-parse", "--abbrev-ref", "HEAD")


def rundir(ts=None):
    if ts is None:
        m = BRANCH_RE.match(branch())
        if not m:
            sys.exit(f"not on a section-doctor/<TS> branch ({branch()}); pass --ts")
        ts = m.group(1)
    path = os.path.join(os.environ.get("TMPDIR", "/tmp"), "section-doctor", ts)
    os.makedirs(os.path.join(path, "assessment"), exist_ok=True)
    return path


def cmd_rundir(a):
    print(rundir(a.ts))


def cmd_mark(a):
    label = " ".join(a.label)
    with open(os.path.join(rundir(), "phase-log"), "a", encoding="utf-8") as f:
        f.write(f"{now()} {a.phase} {label}\n")


def origin_gate():
    url = git("remote", "get-url", "origin")
    if not ORIGIN_RE.search(url):
        sys.exit(f"origin is {url}, not peterdrier/Humans — stop, raise in Needs Peter")


def cmd_push(a):
    origin_gate()
    b = branch()
    subprocess.run(["git", "push", "-u", "origin", b], check=True)


def _num(tok):
    return int(tok) if tok.isdigit() else WORDS[tok.lower()]


COMMENT_PREFIX_RE = re.compile(r"^\s*(?:///?|\*)\s?")


def _rows_under(lines, i):
    """Rows of the table or list that follows line i (blank lines between skipped), else 0.
    Comment prefixes are stripped so a list inside a `//` block counts like one in a doc."""
    lines = [COMMENT_PREFIX_RE.sub("", l) for l in lines]
    j = i + 1
    while j < len(lines) and not lines[j].strip():
        j += 1
    if j >= len(lines):
        return 0
    if lines[j].startswith("|"):
        n = 0
        while j < len(lines) and lines[j].startswith("|"):
            n += TABLE_ROW_RE.match(lines[j]) is not None
            j += 1
        return max(n - 1, 0)  # minus the header row
    if BULLET_RE.match(lines[j]):
        n = 0
        while j < len(lines) and BULLET_RE.match(lines[j]):
            n += 1
            j += 1
        return n
    return 0


HUNK_RE = re.compile(r"^@@ -\S+ \+([0-9]+)")


def fenced_lines(lines):
    """Line numbers (1-based) inside ``` fences — code samples in a doc are not prose."""
    inside, out = False, set()
    for i, l in enumerate(lines, 1):
        if FENCE_RE.match(l):
            inside = not inside
            out.add(i)
        elif inside:
            out.add(i)
    return out


def prose_gate_hits(diff, read_file):
    """(must_fix, advisory) lists of `path: line`. The gate tells prose from code, not
    paths: in .cs only comment lines (tests/ included — a count in a test comment rots like
    any other), in .md nothing inside a fenced block. The rows under a count are read from
    the resulting file (`read_file(path)`), never from the diff — a -U0 diff drops the
    unchanged rows a newly typed count sits above."""
    must, advisory = [], []
    path, added = None, []  # added: (new-file line number, text)

    def flush():
        if path is None:
            return
        cs = path.endswith(".cs")
        if not (cs or path.endswith(PROSE_SUFFIXES)):
            return
        file_lines, fenced = None, None
        for lineno, l in added:
            if cs and not COMMENT_RE.match(l):
                continue
            if path.endswith(".md"):
                if file_lines is None:
                    file_lines = read_file(path).splitlines()
                    fenced = fenced_lines(file_lines)
                if lineno in fenced:
                    continue
            tag = f"{path}: {l}"
            if STRUCTURAL_RE.search(l):
                must.append(tag)
                continue
            m = LOOSE_RE.search(l)
            if not m:
                continue
            if file_lines is None:
                file_lines = read_file(path).splitlines()
            rows = _rows_under(file_lines, lineno - 1)
            counted = (rows and _num(m.group(1)) == rows) or HEADING_RE.match(l)
            if counted:
                must.append(tag)
            else:
                advisory.append(tag)

    lineno = 0
    for l in diff.splitlines():
        if l.startswith("+++ "):
            flush()
            path, added = l[4:].removeprefix("b/"), []
        elif l.startswith("@@"):
            lineno = int(HUNK_RE.match(l).group(1))
        elif l.startswith("+") and not l.startswith("+++"):
            added.append((lineno, l[1:]))
            lineno += 1
        elif not l.startswith("-"):
            lineno += 1
    flush()
    return must, advisory


def staged_file(path):
    return git("show", f":{path}")


def worktree_file(path):
    with open(path, encoding="utf-8", errors="ignore") as f:
        return f.read()


def prose_gate(diff_args, read_file):
    must, advisory = prose_gate_hits(git("diff", "-U0", *diff_args), read_file)
    if advisory:
        print("advisory (a numeral near a plural — read once, no action required):")
        for h in advisory:
            print("  " + h)
    for h in must:
        print(h)
    if must:
        sys.exit(f"prose gate: {len(must)} line(s) type a count of the list under them or a total — delete or list")
    print("prose gate: clean")


def cmd_prose_gate(a):
    if a.base is None:
        prose_gate(["--cached"], staged_file)
    else:
        prose_gate([a.base], worktree_file)


def cmd_commit(a):
    """The gate runs inside the commit so a run cannot skip it; gates.log records each run
    (its own file — a line in the phase log would become a cost-report row)."""
    if not git("diff", "--cached", "--name-only"):
        sys.exit("nothing staged: `git add` first — doctor.py commit stages nothing")
    must, advisory = prose_gate_hits(git("diff", "-U0", "--cached"), staged_file)
    with open(os.path.join(rundir(), "gates.log"), "a", encoding="utf-8") as f:
        f.write(f"{now()} prose-gate must-fix={len(must)} advisory={len(advisory)}\n")
    prose_gate(["--cached"], staged_file)
    msg = ["-F", a.file] if a.file else ["-m", a.message]
    subprocess.run(["git", "commit", *msg], check=True)


def cmd_dispatch_log(a):
    path = os.path.join(rundir(), "assessment", "threads.md")
    new = not os.path.exists(path)
    with open(path, "a", encoding="utf-8") as f:
        if new:
            f.write("| Thread | Model | Agent type | Dispatched |\n|---|---|---|---|\n")
        f.write(f"| {a.thread} | {a.model} | {a.agent_type or '-'} | {now()} |\n")


GENERATED_RE = re.compile(r"(\.Designer\.cs|DbContextModelSnapshot\.cs)$")
THREADS = ("Shape", "Behavior & bugs", "Freshness", "Conformance", "Tests", "Prose & surface",
           "History", "Comments", "Inbox")
RUN_FILE_BLOCKS = ("## Assessment summary", "## Findings", "## Worked", "## Skipped", "## Retro", "## Needs Peter",
                   "## File coverage", "## Threads")  # "## Ranked findings" is accepted for Findings


def inventory(section):
    """[(path, generated?)] — the section, its Contracts leaf, its test project, its guide page."""
    roots = [f"src/Sections/Humans.{section}", f"src/Sections/Humans.{section}.Contracts",
             f"tests/Humans.{section}.Tests", f"docs/guide/{section}.md"]
    paths = git("ls-files", "--", *roots).splitlines()
    return [(x, bool(GENERATED_RE.search(x))) for x in paths if x]


def cmd_inventory(a):
    for path, gen in inventory(a.section):
        print(f"{path}\tgenerated" if gen else path)


def cmd_check_run_file(a):
    """Coverage is a success criterion: the run file names every inventory path with a
    disposition, every thread with how it ran, and every required block. Lists what is
    missing; non-zero if anything is."""
    text = worktree_file(a.path)
    missing = [b for b in RUN_FILE_BLOCKS if b not in text and not (b == "## Findings" and "## Ranked findings" in text)]
    if not re.search(r"Independence check: (pass|fail)", text):
        missing.append("Independence check: pass|fail line")
    for path, _ in inventory(a.section):
        rows = re.findall(r"^[^\n]*`?" + re.escape(path) + r"`?(?![\w/.])[^\n]*$", text, re.M)
        if not rows:
            missing.append(f"coverage row: {path}")
        elif not any(re.search(r"\b(reviewed|changed|generated)\b", r) for r in rows):
            missing.append(f"coverage row: {path} — disposition must contain reviewed, changed or generated")
    for t in THREADS:   # the row exists (runfile writes it) and the run filled how it ran and what it found
        if not re.search(r"^\|\s*" + re.escape(t) + r"\s*\|\s*[^|\s][^|]*\|[^|]*\|\s*[^|\s][^|]*\|", text, re.M):
            missing.append(f"thread row incomplete: {t} (how it ran, findings)")
    for m in missing:
        print("missing: " + m)
    if missing:
        sys.exit(f"run file: {len(missing)} required item(s) missing")
    print("run file: complete")


def cmd_resolve_check(a):
    b = branch()
    if subprocess.run(["git", "cat-file", "-e", a.sha]).returncode != 0:
        sys.exit(f"{a.sha}: no such commit")
    containing = git("branch", "-r", "--contains", a.sha).split()
    if f"origin/{b}" not in containing:
        sys.exit(f"{a.sha} is not on origin/{b} — push first")
    print(f"{a.sha} is on origin/{b}")


# ---- Phase 3–4 extractors: what a thread or reviewer would otherwise read whole ----

def _cs_comments(text):
    """[(line, text)] of every comment in a C# file — `//`, `///`, `/* */` — with string and
    char literals skipped, so a URL inside a string is not a comment."""
    out, in_block = [], False
    for n, line in enumerate(text.splitlines(), 1):
        i, in_str, verbatim = 0, False, False
        if in_block:
            end = line.find("*/")
            if end < 0:
                out.append((n, line.strip()))
                continue
            out.append((n, line[:end].strip()))
            i, in_block = end + 2, False
        while i < len(line):
            c = line[i]
            if in_str:
                if c == "\\" and not verbatim:
                    i += 2
                    continue
                if c == '"':
                    if verbatim and line[i + 1:i + 2] == '"':
                        i += 2
                        continue
                    in_str = False
                i += 1
                continue
            if c == '"':
                in_str, verbatim = True, "@" in line[max(0, i - 2):i]
                i += 1
                continue
            if c == "'":
                j = line.find("'", i + 3 if line[i + 1:i + 2] == "\\" else i + 2)
                i = j + 1 if j > 0 else i + 1
                continue
            if line.startswith("//", i):
                out.append((n, line[i:].strip()))
                break
            if line.startswith("/*", i):
                end = line.find("*/", i + 2)
                if end < 0:
                    out.append((n, line[i:].strip()))
                    in_block = True
                    break
                out.append((n, line[i:end + 2].strip()))
                i = end + 2
                continue
            i += 1
    return [(n, t) for n, t in out if t]


def _block_comments(text, pairs):
    """[(line, text)] of block comments delimited by any of `pairs` ((open, close), …)."""
    out, cur = [], None
    for n, line in enumerate(text.splitlines(), 1):
        i = 0
        while i < len(line):
            if cur:
                end = line.find(cur[1], i)
                if end < 0:
                    out.append((n, line[i:].strip()))
                    break
                out.append((n, line[i:end].strip()))
                i, cur = end + len(cur[1]), None
                continue
            starts = [(line.find(o, i), (o, c)) for o, c in pairs if line.find(o, i) >= 0]
            if not starts:
                break
            pos, cur = min(starts)
            i = pos + len(cur[0])
    return [(n, t) for n, t in out if t]


def _hash_comments(text):
    """`# ...` rows of a yaml file; a `#` inside a quoted scalar is not a comment."""
    rows = []
    for n, line in enumerate(text.splitlines(), 1):
        quote = None
        for i, ch in enumerate(line):
            if quote:
                if ch == quote:
                    quote = None
            elif ch in "\"'":
                quote = ch
            elif ch == "#" and (i == 0 or line[i - 1].isspace()):
                rows.append((n, line[i:].strip()))
                break
    return rows


def file_comments(path):
    """Comment rows of one inventory file, or None when the file type carries no comments."""
    if path.endswith((".cs", ".js", ".ts")):
        return _cs_comments(worktree_file(path))
    if path.endswith((".css", ".scss")):
        return _block_comments(worktree_file(path), (("/*", "*/"),))
    if path.endswith(".cshtml"):   # razor and html comments, plus `//` and `/* */` inside code and script blocks
        text = worktree_file(path)
        rows = _block_comments(text, (("@*", "*@"), ("<!--", "-->"))) + _cs_comments(text)
        return sorted(set(rows))
    if path.endswith((".csproj", ".props", ".targets", ".xml", ".config", ".md", ".html")):
        return _block_comments(worktree_file(path), (("<!--", "-->"),))
    if path.endswith((".yml", ".yaml")):
        return _hash_comments(worktree_file(path))
    return None   # .resx: its only comments are the schema boilerplate every file repeats


def cmd_comments(a):
    """Every comment in the section's code, `path:line: text` — the Comments thread's whole
    input, so it reads a few hundred lines instead of every source file."""
    for path, gen in inventory(a.section):
        rows = None if gen else file_comments(path)
        for n, t in rows or ():
            print(f"{path}:{n}: {t}")


HISTORY_RE = re.compile(
    r"\b(?:used to|previously|formerly|no longer|originally|historically|legacy|"
    r"was (?:moved|renamed|removed|replaced|split|extracted|introduced)|renamed from|replaced by|"
    r"migrated from|the first (?:section|to)|before 20\d\d|as of 20\d\d|post-mortem|retro|"
    r"lane \d+|PR ?#?\d+|Humans#\d+)\b|\b20\d\d-\d\d-\d\d\b|(?<![\w&])#\d{3,5}\b",
    re.IGNORECASE)


def cmd_history(a):
    """Lines that narrate a prior state — dates, PR numbers, 'used to', 'no longer' — across
    the section's docs and code comments: the History thread's candidate list, not its verdict."""
    for path, gen in inventory(a.section):
        if gen:
            continue
        if path.endswith((".md", ".yml", ".yaml")):   # prose narrates history too, not only its comments
            rows = list(enumerate(worktree_file(path).splitlines(), 1))
        else:
            rows = file_comments(path)
        for n, t in rows or ():
            if HISTORY_RE.search(t):
                print(f"{path}:{n}: {t.strip()}")


CODE_GLOBS = ("*.cs", "*.cshtml", "*.resx", "*.json", "*.yml", "*.yaml", "*.js", "*.csproj",
              "*.props", "*.sql", "*.sh", ":(exclude)docs/", ":(exclude).claude/", ":(exclude)memory/")
BLAST_GLOBS = ("*.cs", "*.cshtml", "*.resx", "*.json", "*.yml", "*.yaml", "*.js", "*.csproj", "*.props",
               "*.sql", "*.sh", "*.py", "*.md", ":(exclude)docs/reforge/")
BLAST_CAP = 60
BACKTICK_RE = re.compile(r"`([^`\n]+)`")
LINE_REF_RE = re.compile(r"^((?:[^\s:]+/[^\s:]+|[^\s:]+\.[A-Za-z0-9]{1,6})):(\d+)(?:-\d+)?$")   # a path or a bare file name
IDENT_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]{2,}")
PROSE_TOKEN_RE = re.compile(r"^[a-z][a-z -]*$")   # `keep`, `not a defect`: words, not names
PATH_TOKEN_RE = re.compile(r"^(?:src|tests|docs|memory|\.claude|\.github)/|\.[a-z0-9]{1,6}$")
SECTION_ROOT_RE = re.compile(r"^(src/Sections/Humans\.[A-Za-z0-9]+)/")
SHA_RE = re.compile(r"^[0-9a-f]{7,40}$")
GIT_REF_RE = re.compile(r"^(?:origin|upstream|refs|section-doctor)/|^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+#\d+$")  # branches, issue refs


def grep_hits(needle, globs, word=True, exclude=()):
    """Working-tree `git grep -n -F` lines for `needle` under `globs`, minus files in `exclude`;
    hits under `src/` first, then `tests/`, so the first hit is the one worth citing."""
    p = subprocess.run(["git", "grep", "-n", "-I", "-F"] + (["-w"] if word else []) + ["-e", needle, "--", *globs],
                       capture_output=True, text=True)
    hits = [h for h in p.stdout.splitlines() if h.split(":", 1)[0] not in exclude]
    return sorted(hits, key=lambda h: (not h.startswith("src/"), not h.startswith("tests/")))


def _section_root(doc, section):
    m = SECTION_ROOT_RE.match(doc)
    return m.group(1) if m else f"src/Sections/Humans.{section}" if section else None


def _path_candidates(tok, doc, section):
    """A doc names paths relative to itself, its section root or `src/Sections/` as often as to the repo."""
    tok = tok.rstrip("/")
    root = _section_root(doc, section)
    return ([tok, os.path.join(os.path.dirname(doc), tok), os.path.join("src/Sections", tok)]
            + ([os.path.join(root, tok)] if root else []))


def _resolve_path(tok, doc, section):
    """The tree path a doc's path token names, or None: the candidates first, then a bare file
    name anywhere in the tree, the doc's section first."""
    for c in _path_candidates(tok, doc, section):
        if os.path.exists(c):
            return c
    if "/" not in tok:
        found = sorted(git("ls-files", "--", "**/" + tok).split(),
                       key=lambda f: not f.startswith(_section_root(doc, section) or "\0"))
        if found:
            return found[0]
    return None


def trace_token(tok, doc, section, exclude):
    """(status, detail): ok / MISS / CHECK (a route whose literal is not in the code: read the
    attribute by hand) / skip (a word or an extension, not a name)."""
    if SHA_RE.match(tok):
        ok = subprocess.run(["git", "cat-file", "-e", tok], capture_output=True).returncode == 0
        return ("ok", "commit") if ok else ("MISS", "no such commit")
    if GIT_REF_RE.match(tok):
        return "skip", "a branch or issue reference"
    m = LINE_REF_RE.match(tok)
    if m:
        path = _resolve_path(m.group(1), doc, section)
        if path is None or not os.path.isfile(path):
            return "MISS", "no such file"
        lines, n = worktree_file(path).splitlines(), int(m.group(2))
        if n > len(lines):
            return "MISS", f"{path} has {len(lines)} lines"
        cited = lines[n - 1].strip()
        if not re.search(r"[A-Za-z0-9]", cited):   # a blank line or a bare brace: the cite has drifted
            return "CHECK", f"{path}:{n} is blank or punctuation only — the cited statement moved"
        return "ok", f"{path}:{n}  {cited}"
    if " " in tok or PROSE_TOKEN_RE.match(tok) or (tok.startswith(".") and "/" not in tok) or not IDENT_RE.search(tok) \
            or re.match(r"^[a-z]+:", tok) or "<" in tok or tok.startswith("$"):
        return "skip", "a word, a placeholder, an extension or a URL, not a name"
    if "*" in tok and "/" in tok and not tok.startswith("/"):
        return ("ok", "glob") if any(glob.glob(c, recursive=True) for c in _path_candidates(tok, doc, section)) else ("MISS", "glob matches nothing")
    if "*" in tok and not tok.startswith("/"):
        hits = grep_hits(tok.split("*")[0], CODE_GLOBS, word=False, exclude=exclude)
        return ("ok", f"prefix, {len(hits)} hit(s), first {hits[0]}") if hits else ("MISS", "prefix matches nothing")
    if tok.startswith("/"):
        literal = tok.rstrip("/*")
        for needle in (literal, re.split(r"[{*]", literal)[0].rstrip("/")):
            hits = grep_hits(needle, ("*.cs", "*.cshtml", "*.js"), word=False, exclude=exclude)
            if hits:
                return "ok", f"route, {len(hits)} hit(s) for {needle}, first {hits[0]}"
        return "CHECK", "route literal not in code; read the attribute"
    if PATH_TOKEN_RE.search(tok):
        path = _resolve_path(tok, doc, section)
        return ("ok", path) if path else ("MISS", "no such path")
    hits = grep_hits(tok, CODE_GLOBS, word=False, exclude=exclude)
    if hits:
        return "ok", f"{len(hits)} hit(s), first {hits[0]}"
    first, missing = None, []
    for ident in IDENT_RE.findall(tok):
        h = grep_hits(ident, CODE_GLOBS, word=True, exclude=exclude)
        if not h:
            missing.append(ident)
        first = first or (h[0] if h else None)
    if missing:
        return "MISS", "no hit for " + ", ".join(missing)
    return "ok", f"every segment hits, first {first}"


def cmd_trace(a):
    """The trace gate (3c, Phase 7): every backticked name, route, path and file:line in the
    given docs, resolved against the tree (paths also relative to the doc, to `src/Sections/` and
    to the section named by `--section` or the doc's own location; a hex token is a commit).
    A `file:line` cite prints the cited line's text so its resolution can be eyeballed.
    Prints one line per token; non-zero on any MISS."""
    exclude, seen, misses = set(a.file), set(), 0
    for doc in a.file:
        for tok in BACKTICK_RE.findall(worktree_file(doc)):
            tok = tok.strip()
            if not tok or tok in seen or tok.startswith("-"):
                continue
            seen.add(tok)
            status, detail = trace_token(tok, doc, a.section, exclude)
            if status == "skip" and not a.all:
                continue
            misses += status == "MISS"
            print(f"{status:5} {tok}  ({detail})")
    if misses:
        sys.exit(f"trace: {misses} name(s) do not resolve")
    print("trace: every name resolves")


def blast(symbol):
    return grep_hits(symbol, BLAST_GLOBS, word=True)


def print_blast(symbol, hits, out=print):
    out(f"== {symbol}: {len(hits)} hit(s)")
    for h in hits[:BLAST_CAP]:
        out("  " + h)
    if len(hits) > BLAST_CAP:
        out(f"  ... {len(hits) - BLAST_CAP} more: git grep -n -w -F -e {symbol}")


def cmd_blast(a):
    """Repo-wide, word-bounded `git grep -n` per symbol across code and docs — the blast
    radius a strike bounds before it names a symbol."""
    for s in a.symbol:
        print_blast(s, blast(s))


# Reviewer tier per section: the cost of a wrong approval, not the section's size. A section
# in neither set gets the middle tier. Peter moves sections between rows.
REVIEW_TIERS = (
    ("doctor-reviewer-critical", "fable high", {
        "Users", "Auth", "Backdoor", "Gdpr", "Consent", "Governance", "Finance", "Stripe", "Holded",
        "Tickets", "TicketTailor", "Teams", "Onboarding", "GoogleIntegration", "AuditLog", "Email"}),
    ("doctor-reviewer-light", "opus medium", {
        "Tour", "Guide", "Debug", "Development", "Feedback", "Rideshare", "CityPlanning", "Agent"}),
)
REVIEW_TIER_DEFAULT = ("doctor-reviewer", "opus high")
SYMBOL_RE = re.compile(r"\b[A-Z][A-Za-z0-9]{2,}\b")
UBIQUITOUS = 100   # a removed name with this many hits left is a framework word, not a sweep miss
HEAD_LINES = 15


def reviewer_for(section):
    for agent, model, sections in REVIEW_TIERS:
        if section in sections:
            return agent, model
    return REVIEW_TIER_DEFAULT


def cmd_review_pack(a):
    """Capture the strike for the reviewer gate: the uncommitted diff, the repo-wide blast grep
    of every name the diff removes (run against the edited tree, so the hits are what the
    sweep missed), the head of every touched file, and a brief. Prints the pack directory and
    the reviewer agent to dispatch for this section."""
    root = os.path.join(rundir(), "review")
    os.makedirs(root, exist_ok=True)
    slug = re.sub(r"[^A-Za-z0-9]+", "-", a.what).strip("-").lower()[:40]
    pack = os.path.join(root, f"{len(os.listdir(root)) + 1:02d}-{slug}")
    os.makedirs(pack)
    diff = git("diff", "HEAD")
    untracked = git("ls-files", "--others", "--exclude-standard").split()
    for u in untracked:
        p = subprocess.run(["git", "diff", "--no-index", "--", "/dev/null", u], capture_output=True, text=True)
        diff += "\n" + p.stdout
    if not diff.strip():
        sys.exit("review-pack: the tree has no uncommitted change to review")
    with open(os.path.join(pack, "diff.patch"), "w", encoding="utf-8") as f:
        f.write(diff)
    removed, added = set(), set()
    for l in diff.splitlines():
        if l.startswith("-") and not l.startswith("---"):
            removed.update(SYMBOL_RE.findall(l))
        elif l.startswith("+") and not l.startswith("+++"):
            added.update(SYMBOL_RE.findall(l))
    gone, live, ubiquitous = [], [], []
    for s in sorted(removed - added):
        hits = blast(s)
        if not hits:
            gone.append(s)
        elif len(hits) > UBIQUITOUS:
            ubiquitous.append(s)
        else:
            live.append((s, hits))
    with open(os.path.join(pack, "blast.md"), "w", encoding="utf-8") as f:
        w = lambda s="": f.write(s + "\n")
        w("# Names the diff removes, grepped repo-wide against the edited tree")
        w()
        w("## Still referenced — what the sweep left, each one a question")
        w()
        for s, hits in live:
            print_blast(s, hits, out=w)
        w()
        w("## No reference left: " + (", ".join(gone) or "none"))
        w()
        w("## Skipped as ubiquitous (over %d hits): " % UBIQUITOUS + (", ".join(ubiquitous) or "none"))
    touched = git("diff", "--name-only", "HEAD").split() + untracked
    with open(os.path.join(pack, "heads.md"), "w", encoding="utf-8") as f:
        f.write("# The first %d lines of every touched file, after the edit\n" % HEAD_LINES)
        for t in touched:
            f.write(f"\n## {t}\n\n```\n")
            if os.path.isfile(t):
                f.write("\n".join(worktree_file(t).splitlines()[:HEAD_LINES]) + "\n")
            else:
                f.write("(deleted)\n")
            f.write("```\n")
    agent, model = reviewer_for(a.section)
    top = git("rev-parse", "--show-toplevel")
    with open(os.path.join(pack, "brief.md"), "w", encoding="utf-8") as f:
        f.write(f"# Review: {a.what}\n\nSection: {a.section}\nFinding: {a.finding or '(see prompt)'}\n\n"
                f"Rules: `{top}/.claude/skills/section-doctor/threads/review.md`.\n"
                f"Read in this order: `diff.patch`, `blast.md`, `heads.md`; then the target shape at\n"
                f"`{top}/src/Sections/Humans.{a.section}/Docs/health.md` (load-bearing weirdness) and\n"
                f"`{top}/docs/architecture/code-review-rules.md` for the shape the change collapses into.\n")
    print(f"PACK: {pack}")
    print(f"REVIEWER: {agent} ({model})")


# ---- The run file: header and the two mechanical tables ----

RUN_FILE_HEAD_BLOCKS = ("## Assessment summary", "## Findings", "## Worked", "## Skipped", "## Retro",
                        "## Needs Peter")
COVERAGE_ROW_RE = re.compile(r"^\|\s*`?([^`|]+?)`?\s*\|\s*([^|]*?)\s*\|")
THREAD_ROW_RE = re.compile(r"^\|\s*([^|]+?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|")
MAIN_THREADS = ("Shape", "Behavior & bugs")


def _block(text, heading):
    """(start, end) of the block under `heading` up to the next `## `, or None."""
    m = re.search(r"^" + re.escape(heading) + r"[ \t]*$", text, re.M)
    if not m:
        return None
    nxt = re.search(r"^## ", text[m.end():], re.M)
    return m.start(), (m.end() + nxt.start()) if nxt else len(text)


def _replace_block(text, heading, body):
    span = _block(text, heading)
    block = heading + "\n\n" + body.rstrip("\n") + "\n\n"
    if span is None:
        return text.rstrip("\n") + "\n\n" + block
    return text[:span[0]] + block + text[span[1]:]


def _rows(text, heading, row_re):
    span = _block(text, heading)
    if span is None:
        return {}
    out = {}
    for l in text[span[0]:span[1]].splitlines():
        m = row_re.match(l)
        if m and not set(m.group(2)) <= set("-: ") and m.group(1).lower() not in ("path", "thread"):
            out[m.group(1)] = m.groups()[1:]
    return out


def coverage_table(section, existing):
    """One row per inventory path. `generated` and `changed` (against the branch point,
    uncommitted edits included) are read from git; `reviewed` is the run's own claim and is
    kept where the run already wrote it."""
    base = git("merge-base", "origin/main", "HEAD")
    changed = set(git("diff", "--name-only", base).split()) | set(git("ls-files", "--others", "--exclude-standard").split())
    rows = ["| Path | Disposition |", "|---|---|"]
    for path, gen in inventory(section):
        disp = "generated" if gen else "changed" if path in changed else (existing.get(path) or ("",))[0]
        rows.append(f"| `{path}` | {disp} |")
    return "\n".join(rows)


def threads_table(existing):
    """One row per thread: how it ran and on what, from the dispatch log; findings count kept
    from the run's own row."""
    dispatched = {}
    log = os.path.join(rundir(), "assessment", "threads.md")
    if os.path.exists(log):
        for l in worktree_file(log).splitlines():
            m = THREAD_ROW_RE.match(l)
            if m and m.group(1).lower() != "thread" and not set(m.group(2)) <= set("-: "):
                dispatched[m.group(1).lower().split()[0]] = (m.group(2), m.group(3))
    rows = ["| Thread | How it ran | Model | Findings |", "|---|---|---|---|"]
    for t in THREADS:
        prev = existing.get(t, ("", "", ""))
        d = dispatched.get(t.lower().split()[0])
        if d:
            how, model = (f"subagent (`{d[1]}`)" if d[1] != "-" else "subagent"), d[0]
        elif t in MAIN_THREADS:
            how, model = "main", prev[1]
        else:
            how, model = prev[0], prev[1]
        rows.append(f"| {t} | {how} | {model} | {prev[2]} |")
    return "\n".join(rows)


def cmd_runfile(a):
    """Create the run file (Phase 2) or refresh its mechanical tables (Phase 5). Idempotent:
    the header and the prose blocks are written once and never touched again; `## File
    coverage` and `## Threads` are regenerated from git and the dispatch log each call, keeping
    the dispositions and findings counts the run wrote by hand."""
    if not re.fullmatch(r"[A-Za-z0-9]+", a.section) or not os.path.isdir(f"src/Sections/Humans.{a.section}"):
        sys.exit(f"runfile takes a section name (src/Sections/Humans.<Name>), not a path or a Contracts leaf: {a.section}")
    ts = BRANCH_RE.match(branch())
    if not ts:
        sys.exit(f"not on a section-doctor/<TS> branch ({branch()})")
    ts, date = ts.group(1), ts.group(1)[:10]
    marker = f"branch `section-doctor/{ts}`"
    path = f"docs/health/runs/{date}-{a.section}.md"
    if os.path.exists(path) and marker not in worktree_file(path):
        path = f"docs/health/runs/{date}-{a.section}-{ts[11:15]}Z.md"
    if os.path.exists(path):
        text = worktree_file(path)
    else:
        anchor = git("rev-parse", "--short", git("merge-base", "origin/main", "HEAD"))
        text = (f"# section-doctor — {a.section} — {date}\n\n"
                f"- Invocation: {a.invocation}\n"
                f"- Anchor commit: `{anchor}` (origin/main at branch point); {marker}.\n"
                f"- Budget: {a.budget}.\n"
                f"- PR: pending\n\n" + "".join(b + "\n\n" for b in RUN_FILE_HEAD_BLOCKS))
    text = _replace_block(text, "## File coverage", coverage_table(a.section, _rows(text, "## File coverage", COVERAGE_ROW_RE)))
    text = _replace_block(text, "## Threads", threads_table(_rows(text, "## Threads", THREAD_ROW_RE)))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    print(path)



def main():
    signal.signal(signal.SIGPIPE, signal.SIG_DFL)   # `| head` is not an error
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("rundir"); s.add_argument("--ts"); s.set_defaults(fn=cmd_rundir)
    s = sub.add_parser("mark"); s.add_argument("phase"); s.add_argument("label", nargs="+"); s.set_defaults(fn=cmd_mark)
    s = sub.add_parser("push"); s.set_defaults(fn=cmd_push)
    s = sub.add_parser("prose-gate"); s.add_argument("--base"); s.set_defaults(fn=cmd_prose_gate)
    s = sub.add_parser("commit"); g = s.add_mutually_exclusive_group(required=True)
    g.add_argument("-F", dest="file"); g.add_argument("-m", dest="message"); s.set_defaults(fn=cmd_commit)
    s = sub.add_parser("dispatch-log"); s.add_argument("thread"); s.add_argument("model")
    s.add_argument("agent_type", nargs="?"); s.set_defaults(fn=cmd_dispatch_log)
    s = sub.add_parser("resolve-check"); s.add_argument("sha"); s.set_defaults(fn=cmd_resolve_check)
    s = sub.add_parser("inventory"); s.add_argument("section"); s.set_defaults(fn=cmd_inventory)
    s = sub.add_parser("check-run-file"); s.add_argument("path"); s.add_argument("--section", required=True)
    s.set_defaults(fn=cmd_check_run_file)
    s = sub.add_parser("runfile"); s.add_argument("section"); s.add_argument("--invocation", default="unattended daily run, no arguments")
    s.add_argument("--budget", default="2.5h"); s.set_defaults(fn=cmd_runfile)
    s = sub.add_parser("comments"); s.add_argument("section"); s.set_defaults(fn=cmd_comments)
    s = sub.add_parser("history"); s.add_argument("section"); s.set_defaults(fn=cmd_history)
    s = sub.add_parser("trace"); s.add_argument("file", nargs="+"); s.add_argument("--section", help="resolve section-relative paths (a run file names Docs/X.md)")
    s.add_argument("--all", action="store_true", help="print skipped word tokens too")
    s.set_defaults(fn=cmd_trace)
    s = sub.add_parser("blast"); s.add_argument("symbol", nargs="+"); s.set_defaults(fn=cmd_blast)
    s = sub.add_parser("review-pack"); s.add_argument("section"); s.add_argument("what"); s.add_argument("--finding")
    s.set_defaults(fn=cmd_review_pack)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
