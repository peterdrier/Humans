#!/bin/bash
# Shared helper: enumerate the real service classes under the section and Shell
# Services/ trees, and the names the architecture docs may legitimately use for
# each one. Sourced by dependency-graph.sh and service-data-access-map.sh.
#
# Why this is not a file walk. Both checks used to take every `.cs` basename
# below a Services/ folder as a required doc entry. That treats interfaces, DTOs,
# options, converters and CSV maps as services — on this tree it demanded 393
# nodes and reported 307 of them missing, a number no regeneration could ever
# clear. `sort -u` on basenames also collapsed the five sections that name their
# service class exactly `Service` (Containers, Finance, Holded, Store,
# Settings) into one entry.
#
# What a service is, per docs/architecture/peters-hard-rules.md: "Services must
# derive from IApplicationService" — and "some services are orchestrators",
# whose marker IOrchestrator is a sibling of IApplicationService, not derived
# from it. So the population is derived structurally — a non-abstract,
# non-static class whose base list names an interface that reaches
# IApplicationService or IOrchestrator — not from filenames.
#
# Emits one record per service class:  <primary>|<name1,name2,...>|<file>
#   primary — the name to report when the doc is missing it
#   names   — every name the docs may key the class under; a doc entry under ANY
#             of them counts as present
# Callers may set SKIP_NAMES (space separated) and SKIP_PREFIXES (space
# separated) before sourcing-and-calling to drop stragglers.

# Interfaces that reach IApplicationService, directly or through another
# interface (IFooService : IFooServiceRead : IApplicationService) — plus the two
# markers themselves, because a section that publishes no contract of its own
# implements the marker directly and would otherwise enumerate as nothing at
# all. Store is the first: retiring IStoreServiceRead left `Service :
# IApplicationService`, and both checks went on reporting PASS while silently
# covering one section less.
#
# Files are flattened before matching, exactly like the class pass below. A
# line-oriented grep sees only the line carrying the `interface` keyword, so a
# declaration whose base list wraps onto the next line reads as having no bases
# at all — which silently dropped INotificationInboxService and
# IShiftManagementService, and with them NotificationInboxService and
# ShiftManagementService, out of both checks.
service_interfaces() {
  local decls seed pat new merged i
  decls=$(find src -name '*.cs' -type f -not -path '*/obj/*' -not -path '*/bin/*' -print0 \
          | xargs -0 awk 'FNR==1{printf "\n"} {printf "%s ", $0}' \
          | grep -oE 'interface +I[A-Za-z0-9_]+[^{;]*' \
          | sed -E 's/interface +//' | tr -d '\r')
  seed=$(printf 'IApplicationService\nIOrchestrator\n%s\n' \
           "$(echo "$decls" | grep -E ':.*\b(IApplicationService|IOrchestrator)\b' | sed -E 's/[ <:].*//')" \
         | grep -v '^$' | sort -u)
  for i in 1 2 3 4; do
    pat=$(echo "$seed" | paste -sd'|')
    new=$(echo "$decls" | grep -E ":.*\b($pat)\b" | sed -E 's/[ <:].*//' | sort -u)
    merged=$(printf '%s\n%s\n' "$seed" "$new" | sort -u)
    [ "$merged" = "$seed" ] && break
    seed="$merged"
  done
  echo "$seed"
}

service_classes() {
  # Single awk pass over every candidate file. A per-file shell loop spawns
  # thousands of subprocesses, which takes minutes under Git Bash on Windows;
  # this form runs in seconds everywhere.
  local pat
  pat=$(service_interfaces | paste -sd'|')

  find src/Sections src/Humans.Web/Services -path "*/Services/*" -name "*.cs" -type f -print0 \
    | xargs -0 awk -v ifpat="^(${pat})\$" \
                   -v skip_names="${SKIP_NAMES:-}" -v skip_prefixes="${SKIP_PREFIXES:-}" '
    BEGIN {
      nskip = split(skip_names, sn, " ")
      for (i = 1; i <= nskip; i++) if (sn[i] != "") skipname[sn[i]] = 1
      npfx = split(skip_prefixes, pfx, " ")
    }
    # Flatten each file so multi-line primary constructors and base lists parse
    # as a single declaration, then emit its service-class records.
    function emit(text, file,   decl, name, mods, bases, nb, part, b, ifaces, ni,
                  cand, names, primary, i, j, out, section, marked) {
      # `Humans.Store/Services/...` -> `Store`. Only needed for a class whose
      # sole service interface is a bare marker, where the interface name says
      # nothing about which section it belongs to.
      section = ""
      if (match(file, /Humans\.[A-Za-z0-9_]+\/Services\//))
        section = substr(file, RSTART + 7, RLENGTH - 17)

      while (match(text, /(public|internal)[a-zA-Z ]*class [A-Za-z0-9_]+[^{;]*/)) {
        decl = substr(text, RSTART, RLENGTH)
        text = substr(text, RSTART + RLENGTH)

        match(decl, /class [A-Za-z0-9_]+/)
        name = substr(decl, RSTART + 6, RLENGTH - 6)
        mods = substr(decl, 1, index(decl, "class ") - 1)
        # Abstract bases and static helper classes are never DI-registered nodes.
        if (mods ~ /abstract|static/) continue
        # No base list at all — a DTO, options or settings class, never a service.
        if (index(decl, ":") == 0) continue

        if (name in skipname) continue
        for (i = 1; i <= npfx; i++)
          if (pfx[i] != "" && index(name, pfx[i]) == 1) { name = ""; break }
        if (name == "") continue

        bases = substr(decl, index(decl, ":") + 1)
        ni = 0; delete ifaces
        nb = split(bases, part, ",")
        for (i = 1; i <= nb; i++) {
          b = part[i]
          sub(/[<(].*/, "", b)
          gsub(/[^A-Za-z0-9_]/, "", b)
          if (b ~ ifpat) ifaces[++ni] = b
        }
        # Implements no service interface: a helper, converter or state holder
        # that happens to live under Services/.
        if (ni == 0) continue

        # Docs key a service by its interface, not its class: Holded`s `Service`
        # class is documented as HoldedService. Read-split boundaries collapse
        # onto the owning service node (dependency-graph.md, "How to read"), so
        # IStoreServiceRead also offers StoreService.
        #
        # A bare marker is the exception: IApplicationService names every
        # service in the tree, so it is useless as a key. Such a class is keyed
        # by its own name — except where that name is the bare `Service` five
        # sections share, which needs the section to disambiguate
        # (Humans.Store/Services/Service.cs -> StoreService).
        delete names; names[name] = 1
        marked = ""
        for (i = 1; i <= ni; i++) {
          cand = ifaces[i]
          if (cand == "IApplicationService" || cand == "IOrchestrator") {
            if (section != "") names[section "Service"] = 1
            cand = (name == "Service") ? (section == "" ? "" : section "Service") : name
            if (cand == "") continue
            names[cand] = 1
            if (marked == "") marked = cand
            continue
          }
          sub(/^I/, "", cand)
          names[cand] = 1
          sub(/Read$/, "", cand)
          names[cand] = 1
        }
        primary = ifaces[1]
        if (primary == "IApplicationService" || primary == "IOrchestrator") primary = marked
        else sub(/^I/, "", primary)
        # A marker-only class outside a Humans.<Section>/Services/ path has no
        # derivable key; enumerating it under the marker would be worse than not.
        if (primary == "") continue
        out = ""
        j = asorti(names, sorted)
        for (i = 1; i <= j; i++) out = out (i > 1 ? "," : "") sorted[i]
        print primary "|" out "|" file
      }
    }
    FNR == 1 { if (buf != "") emit(buf, prev); buf = ""; prev = FILENAME }
    { buf = buf " " $0 }
    END { if (buf != "") emit(buf, prev) }
  ' | sort -u
}
