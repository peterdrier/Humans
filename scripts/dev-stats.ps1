# Generates src/Humans.Web/wwwroot/data/dev-stats.json for the /About engineering panel.
# Run from the repo root before a demo; commit the result. Never runs at app runtime.
$ErrorActionPreference = 'Stop'

# Native failures do not terminate PowerShell pipelines. Check each Git result before
# measuring or parsing it; grep exit 1 is a valid empty match set.
function Invoke-StatsGit {
    param([string[]] $GitArguments, [switch] $AllowNoMatches)
    $result = & git @GitArguments
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0 -and -not ($AllowNoMatches -and $gitExitCode -eq 1)) {
        throw "git failed (exit ${gitExitCode}): $($GitArguments -join ' '); snapshot not written"
    }
    $result
}

$repoRoot = Invoke-StatsGit -GitArguments @('rev-parse', '--show-toplevel')

$totalCommits = [int](Invoke-StatsGit -GitArguments @('rev-list', '--count', 'HEAD'))

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
$testCount = (Invoke-StatsGit -GitArguments @('grep', '-E', '^\s*\[(Fact|Theory|HumansFact|HumansTheory)', '--', 'tests/*.cs') -AllowNoMatches | Measure-Object -Line).Lines

# Analyzer rules: distinct HUMxxxx diagnostic ids in the analyzer project.
$analyzerRuleCount = (Invoke-StatsGit -GitArguments @('grep', '-ohE', 'HUM[0-9]{4}', '--', 'src/Humans.Analyzers/*.cs') -AllowNoMatches | Sort-Object -Unique | Measure-Object).Count

# Section count: a project is a section when its Section.cs implements ISection (SectionAttribute
# was retired in peterdrier/Humans#1330). The class line can wrap, so match the file, not the line.
$sectionCount = (Invoke-StatsGit -GitArguments @('grep', '-lE', '\bISection\b', '--', 'src/Sections/*/Section.cs') -AllowNoMatches | Measure-Object -Line).Lines

# Per-author commit + line counts over the whole history. %aN applies .mailmap, so one person
# committing under two names or emails is counted once. Claude-authored commits are AI work,
# already reported by the co-author percentage, so they are not a contributor row.
$authors = @{}
$current = $null
Invoke-StatsGit -GitArguments @('log', '--numstat', '--format=AUTHOR:%aN') | ForEach-Object {
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
$contributors = $authors.GetEnumerator() | Where-Object { $_.Key -notmatch '\[bot\]$' -and $_.Key -ne 'Claude' } | Sort-Object { $_.Value.added } -Descending | ForEach-Object {
    [ordered]@{ name = $_.Key; commits = $_.Value.commits; linesAdded = $_.Value.added; linesDeleted = $_.Value.deleted }
}

# Claude co-authorship: count COMMITS with at least one Claude trailer, not matching trailer
# LINES -- some squash commits carry several Co-Authored-By trailers (multi-agent sessions),
# so counting lines double/triple-counts a single commit.
$claudeCommits = 0
$sawClaude = $false
Invoke-StatsGit -GitArguments @('log', '--format=@@COMMIT@@%n%(trailers:key=Co-Authored-By,valueonly)') | ForEach-Object {
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
    generatedDate = (Invoke-StatsGit -GitArguments @('log', '-1', '--format=%cs'))
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
