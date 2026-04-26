Read Directory.Build.props at repo root. Find the <NoWarn> property and any
per-rule <Rule Id=...> elements. List each suppression as `- \`<RULE_ID>\` -
<one-line description>`. Use Roslyn analyzer documentation knowledge to fill
the description (e.g., MA0048 = "File name must match type name"). If a rule
ID is unknown, write "TBD: look up rule description".
