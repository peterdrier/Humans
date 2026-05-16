"""i18n audit and fix script for SharedResource .resx files."""
import re
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path("src/Humans.Web/Resources")
DEFAULT_FILE = ROOT / "SharedResource.resx"
LOCALES = {
    "es": ROOT / "SharedResource.es.resx",
    "de": ROOT / "SharedResource.de.resx",
    "fr": ROOT / "SharedResource.fr.resx",
    "it": ROOT / "SharedResource.it.resx",
    "ca": ROOT / "SharedResource.ca.resx",
}
REPORT_FILE = Path("i18n-audit-report.md")

# Matches single-line <data name="X" xml:space="preserve"><value>Y</value></data>
# Also handles optional <comment>...</comment> after <value>
DATA_LINE_RE = re.compile(r'^\s*<data\s+name="([^"]+)"\s+xml:space="preserve"><value>(.*?)</value>(?:<comment>(.*?)</comment>)?</data>\s*$')
PLACEHOLDER_RE = re.compile(r'\{(\d+)\}')


def parse_resx(path):
    """Parse a .resx file, returning dict of keys, values, comments, and structural info."""
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines()

    keys = []
    values = {}
    comments = {}
    data_lines = {}
    first_data_idx = -1
    last_data_idx = -1

    for idx, line in enumerate(lines):
        m = DATA_LINE_RE.match(line)
        if m:
            key = m.group(1)
            val = m.group(2) if m.group(2) is not None else ""
            cmt = m.group(3) if m.group(3) is not None else ""
            if key not in values:
                keys.append(key)
            values[key] = val
            comments[key] = cmt
            data_lines[key] = line
            if first_data_idx == -1:
                first_data_idx = idx
            last_data_idx = idx

    prefix_lines = lines[:first_data_idx] if first_data_idx >= 0 else lines
    suffix_lines = lines[last_data_idx + 1:] if last_data_idx >= 0 else []

    return {
        "path": path,
        "text": text,
        "lines": lines,
        "keys": keys,
        "values": values,
        "comments": comments,
        "data_lines": data_lines,
        "first_data_idx": first_data_idx,
        "last_data_idx": last_data_idx,
        "prefix_lines": prefix_lines,
        "suffix_lines": suffix_lines,
    }


def placeholder_sig(value):
    """Get sorted tuple of placeholder indices found in value."""
    return tuple(sorted(PLACEHOLDER_RE.findall(value or "")))


def fix_locale(default_info, locale_info):
    """Fix locale file: add missing keys, remove orphans, preserve order from default."""
    default_keys = set(default_info["keys"])
    locale_keys = set(locale_info["keys"])

    missing = sorted(default_keys - locale_keys)
    orphaned = sorted(locale_keys - default_keys)

    # Build identical-value and placeholder-mismatch lists
    identical = []
    placeholder_mismatches = []
    for key in default_info["keys"]:
        if key not in locale_info["values"]:
            continue
        dv = default_info["values"].get(key, "")
        lv = locale_info["values"].get(key, "")
        if dv == lv and dv != "":
            identical.append(key)
        if placeholder_sig(dv) != placeholder_sig(lv):
            placeholder_mismatches.append(key)

    # Build the template lines from default (between first and last data lines inclusive)
    template_lines = default_info["lines"][default_info["first_data_idx"]:default_info["last_data_idx"] + 1]

    # Reconstruct locale file: prefix + data lines in default order + suffix
    out_lines = list(locale_info["prefix_lines"])
    for line in template_lines:
        m = DATA_LINE_RE.match(line)
        if m:
            key = m.group(1)
            if key in locale_info["data_lines"]:
                # Keep existing locale translation
                out_lines.append(locale_info["data_lines"][key])
            else:
                # Missing key: add with English value as placeholder
                out_lines.append(default_info["data_lines"][key])
        else:
            # Comment or blank line from default template - preserve
            out_lines.append(line)
    out_lines.extend(locale_info["suffix_lines"])

    # Detect line ending
    newline = "\r\n" if "\r\n" in locale_info["text"] else "\n"
    new_text = newline.join(out_lines)
    # Ensure file ends with newline if original did
    if locale_info["text"].endswith(("\n", "\r")) and not new_text.endswith(newline):
        new_text += newline

    locale_info["path"].write_text(new_text, encoding="utf-8")

    return {
        "missing_added": missing,
        "orphaned_removed": orphaned,
        "identical_values": identical,
        "placeholder_mismatches": placeholder_mismatches,
        "final_key_count": len(default_info["keys"]),
    }


def build_report(default_info, results):
    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%SZ")
    lines = []
    lines.append("# i18n Audit Report")
    lines.append("")
    lines.append(f"Generated: {now}")
    lines.append("")
    lines.append("## Scope")
    lines.append("")
    lines.append(f"- Default file: `{DEFAULT_FILE.as_posix()}`")
    for locale, path in LOCALES.items():
        lines.append(f"- Locale `{locale}`: `{path.as_posix()}`")
    lines.append("")
    lines.append("## Default Key Summary")
    lines.append("")
    lines.append(f"- Total default keys: **{len(default_info['keys'])}**")
    lines.append("")

    total_missing = 0
    total_orphaned = 0
    total_identical = 0
    total_ph = 0

    for locale in ["es", "de", "fr", "it", "ca"]:
        r = results[locale]
        total_missing += len(r["missing_added"])
        total_orphaned += len(r["orphaned_removed"])
        total_identical += len(r["identical_values"])
        total_ph += len(r["placeholder_mismatches"])

        lines.append(f"## Locale: `{locale}`")
        lines.append("")
        lines.append(f"| Metric | Count |")
        lines.append(f"|--------|-------|")
        lines.append(f"| Missing keys added | **{len(r['missing_added'])}** |")
        lines.append(f"| Orphaned keys removed | **{len(r['orphaned_removed'])}** |")
        lines.append(f"| Identical to English (possible untranslated) | **{len(r['identical_values'])}** |")
        lines.append(f"| Placeholder mismatches | **{len(r['placeholder_mismatches'])}** |")
        lines.append(f"| Final key count | **{r['final_key_count']}** |")
        lines.append("")

        if r["missing_added"]:
            lines.append("### Missing keys added (English placeholder)")
            lines.append("")
            for k in r["missing_added"]:
                lines.append(f"- `{k}`")
            lines.append("")

        if r["orphaned_removed"]:
            lines.append("### Orphaned keys removed")
            lines.append("")
            for k in r["orphaned_removed"]:
                lines.append(f"- `{k}`")
            lines.append("")

        if r["identical_values"]:
            lines.append("### Possibly untranslated (value identical to English)")
            lines.append("")
            for k in r["identical_values"]:
                lines.append(f"- `{k}`")
            lines.append("")

        if r["placeholder_mismatches"]:
            lines.append("### Placeholder mismatches")
            lines.append("")
            for k in r["placeholder_mismatches"]:
                dv = default_info["values"].get(k, "")
                lines.append(f"- `{k}`: default has `{placeholder_sig(dv)}`, locale differs")
            lines.append("")

    lines.append("## Summary")
    lines.append("")
    lines.append(f"| Metric | Total across all locales |")
    lines.append(f"|--------|------------------------|")
    lines.append(f"| Missing keys added | **{total_missing}** |")
    lines.append(f"| Orphaned keys removed | **{total_orphaned}** |")
    lines.append(f"| Identical to English | **{total_identical}** |")
    lines.append(f"| Placeholder mismatches | **{total_ph}** |")
    lines.append("")

    return "\n".join(lines) + "\n"


def main():
    default_info = parse_resx(DEFAULT_FILE)
    print(f"Default keys: {len(default_info['keys'])}")

    results = {}
    for locale, path in LOCALES.items():
        locale_info = parse_resx(path)
        print(f"\n{locale}: {len(locale_info['keys'])} keys before fix")
        results[locale] = fix_locale(default_info, locale_info)
        r = results[locale]
        print(f"  +{len(r['missing_added'])} missing added")
        print(f"  -{len(r['orphaned_removed'])} orphaned removed")
        print(f"  ={len(r['identical_values'])} identical to English")
        print(f"  !{len(r['placeholder_mismatches'])} placeholder mismatches")
        print(f"  Final: {r['final_key_count']} keys")

    report = build_report(default_info, results)
    REPORT_FILE.write_text(report, encoding="utf-8")
    print(f"\nReport written to {REPORT_FILE}")


if __name__ == "__main__":
    main()
