# Generates src/Humans.Web/wwwroot/data/dev-stats.json for the /About engineering panel.
# Run from the repo root before a demo; commit the result. Never runs at app runtime.
$ErrorActionPreference = 'Stop'

$repoRoot = git rev-parse --show-toplevel

$totalCommits = [int](git rev-list --count HEAD)

# Merged PRs on the fork (dev flow) + closed issues on upstream (the tracker).
# gh caps list output; use the search API count instead for real totals.
# $ErrorActionPreference does not make native-command failures terminating, so a dead
# gh (no auth, no network) would cast empty output to 0 and silently ship a bogus
# snapshot — check $LASTEXITCODE and abort before anything is written.
$mergedPrs = [int](gh api "search/issues?q=repo:peterdrier/Humans+is:pr+is:merged&per_page=1" --jq '.total_count')
if ($LASTEXITCODE -ne 0) { throw "gh api failed (merged PRs); snapshot not written" }
$closedIssues = [int](gh api "search/issues?q=repo:nobodies-collective/Humans+is:issue+is:closed&per_page=1" --jq '.total_count')
if ($LASTEXITCODE -ne 0) { throw "gh api failed (closed issues); snapshot not written" }

# Test count: attribute occurrences across test sources (HumansFact/HumansTheory included).
$testCount = (git grep -E '^\s*\[(Fact|Theory|HumansFact|HumansTheory)' -- 'tests/*.cs' | Measure-Object -Line).Lines

# Analyzer rules: distinct HUMxxxx diagnostic ids in the analyzer project.
$analyzerRuleCount = (git grep -ohE 'HUM[0-9]{4}' -- 'src/Humans.Analyzers/*.cs' | Sort-Object -Unique | Measure-Object).Count

# Section count: [assembly: Section("...")] declarations under src/Sections. Line-anchored so
# doc-comment mentions of the attribute (e.g. Scanner's own docs) don't inflate the count.
# G5 moves land weekly — this is generated, never hand-maintained.
$sectionCount = (git grep -E '^\[assembly: Section\(' -- 'src/Sections/*' | Measure-Object -Line).Lines

# Per-author commit + line counts over the whole history.
$authors = @{}
$current = $null
git log --numstat --format='AUTHOR:%an' | ForEach-Object {
    if ($_ -like 'AUTHOR:*') {
        $current = $_.Substring(7)
        if (-not $authors.ContainsKey($current)) {
            $authors[$current] = @{ commits = 0; added = 0; deleted = 0 }
        }
        $authors[$current].commits++
    }
    elseif ($_ -match '^(\d+)\t(\d+)\t') {
        $authors[$current].added += [int]$Matches[1]
        $authors[$current].deleted += [int]$Matches[2]
    }
}
$contributors = $authors.GetEnumerator() | Where-Object { $_.Key -notmatch '\[bot\]$' } | Sort-Object { $_.Value.added } -Descending | ForEach-Object {
    [ordered]@{ name = $_.Key; commits = $_.Value.commits; linesAdded = $_.Value.added; linesDeleted = $_.Value.deleted }
}

# Claude co-authorship: count COMMITS with at least one Claude trailer, not matching trailer
# LINES -- some squash commits carry several Co-Authored-By trailers (multi-agent sessions),
# so counting lines double/triple-counts a single commit.
$claudeCommits = 0
$sawClaude = $false
git log --format='@@COMMIT@@%n%(trailers:key=Co-Authored-By,valueonly)' | ForEach-Object {
    if ($_ -eq '@@COMMIT@@') {
        if ($sawClaude) { $claudeCommits++ }
        $sawClaude = $false
    }
    elseif ($_ -match 'Claude') {
        $sawClaude = $true
    }
}
if ($sawClaude) { $claudeCommits++ }
$claudePercent = [math]::Round(100.0 * $claudeCommits / $totalCommits)

$out = [ordered]@{
    generatedDate = (git log -1 --format=%cs)
    totalCommits = $totalCommits
    mergedPrs = $mergedPrs
    closedIssues = $closedIssues
    testCount = $testCount
    analyzerRuleCount = $analyzerRuleCount
    sectionCount = $sectionCount
    contributors = @($contributors)
    claudeCoauthoredCommitPercent = $claudePercent
}
$json = $out | ConvertTo-Json -Depth 4
$dataDir = Join-Path $repoRoot "src/Humans.Web/wwwroot/data"
New-Item -ItemType Directory -Force $dataDir | Out-Null
[System.IO.File]::WriteAllText((Join-Path $dataDir "dev-stats.json"), "$json`n")
Write-Host "Wrote dev-stats.json: $totalCommits commits, $mergedPrs PRs, $closedIssues issues, $testCount tests, $analyzerRuleCount analyzer rules, $sectionCount sections"
