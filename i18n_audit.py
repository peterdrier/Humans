"""i18n audit and fix script for SharedResource .resx files."""
import re
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET

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

PLACEHOLDER_RE = re.compile(r'\{(\d+)\}')

def parse_resx_robust(path):
    """Parse a .resx file using ET, preserving as much as possible."""
    # We need to read as bytes and then decode to handle BOM correctly if present
    content = path.read_text(encoding="utf-8")
    
    # ET.fromstring can handle the XML structure
    root = ET.fromstring(content)
    
    data_map = {} # key -> (value, comment)
    keys = []
    
    for entry in root.findall('data'):
        name = entry.get('name')
        value_node = entry.find('value')
        comment_node = entry.find('comment')
        
        value = value_node.text if value_node is not None else ""
        comment = comment_node.text if comment_node is not None else ""
        
        data_map[name] = (value, comment)
        keys.append(name)
        
    return {
        "path": path,
        "keys": keys,
        "data_map": data_map,
        "root": root
    }

def placeholder_sig(value):
    """Get sorted tuple of placeholder indices found in value."""
    return tuple(sorted(PLACEHOLDER_RE.findall(value or "")))

def fix_locale_robust(default_info, locale_info):
    """Fix locale file: add missing keys, remove orphans, preserve order from default."""
    default_keys = default_info["keys"]
    locale_data = locale_info["data_map"]
    
    missing = []
    orphaned = [k for k in locale_info["keys"] if k not in default_info["data_map"]]
    identical = []
    placeholder_mismatches = []
    
    # Build new root
    new_root = ET.Element('root')
    # Copy non-data elements (schema, resheader, etc)
    # This is a bit tricky with ET if we want to preserve EVERYTHING.
    # But usually resheader and schema are at the beginning.
    
    # Actually, a simpler way to preserve headers is to just modify the existing root
    # or create a new one and copy the first few elements.
    
    # Let's try to just use the default_info's root structure for the headers.
    import copy
    new_root = copy.deepcopy(default_info["root"])
    
    # Remove all existing data elements from the new_root
    for data in new_root.findall('data'):
        new_root.remove(data)
        
    for key in default_keys:
        dv, dc = default_info["data_map"][key]
        
        if key in locale_data:
            lv, lc = locale_data[key]
            # Use existing locale data
            value_to_use = lv
            comment_to_use = lc
            
            if dv == lv and dv != "":
                identical.append(key)
            if placeholder_sig(dv) != placeholder_sig(lv):
                placeholder_mismatches.append(key)
        else:
            # Missing key
            missing.append(key)
            value_to_use = dv
            comment_to_use = dc
            
        # Create new data element
        data_el = ET.SubElement(new_root, 'data', {'name': key, 'xml:space': 'preserve'})
        value_el = ET.SubElement(data_el, 'value')
        value_el.text = value_to_use
        if comment_to_use:
            comment_el = ET.SubElement(data_el, 'comment')
            comment_el.text = comment_to_use
            
    # Write back
    # To maintain formatting (newlines, indents), we'd need minidom or similar.
    # But let's just use ET and maybe some basic pretty printing.
    
    def indent(elem, level=0):
        i = "\n" + level*"  "
        if len(elem):
            if not elem.text or not elem.text.strip():
                elem.text = i + "  "
            if not elem.tail or not elem.tail.strip():
                elem.tail = i
            for elem in elem:
                indent(elem, level+1)
            if not elem.tail or not elem.tail.strip():
                elem.tail = i
        else:
            if level and (not elem.tail or not elem.tail.strip()):
                elem.tail = i

    indent(new_root)
    tree = ET.ElementTree(new_root)
    
    with open(locale_info["path"], "wb") as f:
        f.write(b'<?xml version="1.0" encoding="utf-8"?>\n')
        tree.write(f, encoding="utf-8", xml_declaration=False)

    return {
        "missing_added": missing,
        "orphaned_removed": orphaned,
        "identical_values": identical,
        "placeholder_mismatches": placeholder_mismatches,
        "final_key_count": len(default_keys),
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
                dv, dc = default_info["data_map"].get(k, ("", ""))
                lv, lc = results[locale]["data_map_after"].get(k, ("", ""))
                lines.append(f"- `{k}`: default has `{placeholder_sig(dv)}`, locale has `{placeholder_sig(lv)}`")
            lines.append("")

    lines.append("## Summary")
    lines.append("")
    lines.append("| Metric | Total across all locales |")
    lines.append("|--------|------------------------|")
    lines.append(f"| Missing keys added | **{total_missing}** |")
    lines.append(f"| Orphaned keys removed | **{total_orphaned}** |")
    lines.append(f"| Identical to English | **{total_identical}** |")
    lines.append(f"| Placeholder mismatches | **{total_ph}** |")
    lines.append("")

    return "\n".join(lines)

def main():
    if not DEFAULT_FILE.exists():
        print(f"Error: Default file {DEFAULT_FILE} not found.")
        sys.exit(1)

    print(f"Default keys: {len(parse_resx_robust(DEFAULT_FILE)['keys'])}")
    default_info = parse_resx_robust(DEFAULT_FILE)

    results = {}
    for locale, path in LOCALES.items():
        if not path.exists():
            # Create empty resx if it doesn't exist
            # For now assume they exist as per file listing
            pass
        
        locale_info = parse_resx_robust(path)
        res = fix_locale_robust(default_info, locale_info)
        
        # Add the fixed data map for report mismatch check
        res["data_map_after"] = parse_resx_robust(path)["data_map"]
        results[locale] = res
        
        print(f"\n{locale}: {len(locale_info['keys'])} keys before fix")
        print(f"  +{len(res['missing_added'])} missing added")
        print(f"  -{len(res['orphaned_removed'])} orphaned removed")
        print(f"  ={len(res['identical_values'])} identical to English")
        print(f"  !{len(res['placeholder_mismatches'])} placeholder mismatches")
        print(f"  Final: {res['final_key_count']} keys")

    report = build_report(default_info, results)
    REPORT_FILE.write_text(report, encoding="utf-8")
    print(f"\nReport written to {REPORT_FILE}")

if __name__ == "__main__":
    main()
