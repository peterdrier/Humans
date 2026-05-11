param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutputDirectory = 'local/test-utility',
    [int]$Top = 40,
    [string]$StrykerReport = ''
)

$ErrorActionPreference = 'Stop'

function Convert-ToRepoPath {
    param([string]$Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not $rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $rootFull += [System.IO.Path]::DirectorySeparatorChar
    }

    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $rootUri = New-Object System.Uri($rootFull)
    $pathUri = New-Object System.Uri($pathFull)
    $relative = [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
    return $relative.Replace('\', '/')
}

function Count-Matches {
    param(
        [string]$Text,
        [string]$Pattern
    )
    return [regex]::Matches($Text, $Pattern).Count
}

function Get-Category {
    param([string]$RepoPath)
    $parts = $RepoPath -split '/'
    if ($parts.Length -ge 3) {
        return $parts[2]
    }
    return ''
}

function Get-TestSubjectName {
    param([string]$RepoPath)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($RepoPath)
    if ($name.EndsWith('Tests', [StringComparison]::Ordinal)) {
        return $name.Substring(0, $name.Length - 'Tests'.Length)
    }
    if ($name.EndsWith('Test', [StringComparison]::Ordinal)) {
        return $name.Substring(0, $name.Length - 'Test'.Length)
    }
    return $name
}

function Find-ProductionMatches {
    param([string]$SubjectName)
    if ([string]::IsNullOrWhiteSpace($SubjectName)) {
        return @()
    }

    $pattern = "$SubjectName.cs"
    return @(Get-ChildItem -Path (Join-Path $Root 'src') -Recurse -Filter $pattern -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\|\\Migrations\\|\.Designer\.cs$|HumansDbContextModelSnapshot\.cs$' } |
        ForEach-Object { Convert-ToRepoPath $_.FullName })
}

$testFiles = Get-ChildItem -Path (Join-Path $Root 'tests') -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '\\bin\\|\\obj\\' }

$rows = foreach ($file in $testFiles) {
    $text = Get-Content -Raw -Path $file.FullName
    $repoPath = Convert-ToRepoPath $file.FullName
    $lineCount = ($text -split "`n").Count
    $testCount = Count-Matches $text '(?m)^\s*\[(HumansFact|Fact|HumansTheory|Theory)\b'
    $classCount = Count-Matches $text '\b(public\s+)?(sealed\s+|static\s+|abstract\s+|partial\s+)*class\s+\w+'
    $assertionCount = Count-Matches $text '(\.Should\(|\bAssert\.|\bReceived\(|\bDidNotReceive\(|\bVerify\(|\bMustHaveHappened)'
    $weakAssertionCount = Count-Matches $text '(\.Should\(\)\.(NotBeNull|BeNull|BeOfType|BeAssignableTo|NotThrow|BeTrue|BeFalse)\b|\bAssert\.(NotNull|Null|IsType|IsAssignableFrom|True|False)\b)'
    $substituteCount = Count-Matches $text '\bSubstitute\.For<|\bMock<|\bNSubstitute\.'
    $reflectionCount = Count-Matches $text '\btypeof\(|\.Get(Constructor|Method|Property|Properties|Interfaces|GenericArguments|CustomAttributes)|\.Namespace\b|\.Assembly\b'
    $diCount = Count-Matches $text '\bServiceCollection\b|\bBuildServiceProvider\b|\bGetRequiredService\b|\bIServiceCollection\b'
    $snapshotCount = Count-Matches $text '\bEnum\.GetValues\b|\.ToString\(\)\.Should\(\)\.Be\(|\.Should\(\)\.BeEquivalentTo\(new\[\]'
    $ratchetCount = Count-Matches $text '\bRatchetTestRunner\.Run\('
    $assertionCount += $ratchetCount
    $subject = Get-TestSubjectName $repoPath
    $productionMatches = Find-ProductionMatches $subject
    $category = Get-Category $repoPath
    $isPolicyRatchet = $category -eq 'Architecture' -and $ratchetCount -gt 0
    $isDiCycleSafetyNet =
        $repoPath -match 'DependencyCycle|DiResolution' -or
        ($diCount -ge 3 -and $text -match 'cycle|Resolves_When|Resolve_When|validateScopes')

    $score = 0
    $reasons = New-Object System.Collections.Generic.List[string]

    if ($testCount -eq 0) {
        $score += 15
        $reasons.Add('helper-or-fixture-no-tests')
    }
    else {
        $assertionsPerTest = [math]::Round($assertionCount / [math]::Max(1, $testCount), 2)
        $linesPerTest = [math]::Round($lineCount / [math]::Max(1, $testCount), 1)

        if ($isDiCycleSafetyNet) {
            $score += 8
            $reasons.Add('di-cycle-safety-net')
        }
        elseif ($isPolicyRatchet) {
            $score += 18
            $reasons.Add('policy-ratchet-review-for-consolidation')
        }
        else {
            if ($assertionCount -lt $testCount) {
                $score += 25
                $reasons.Add('fewer-assertions-than-tests')
            }
            elseif ($assertionsPerTest -lt 1.25) {
                $score += 10
                $reasons.Add('low-assertion-density')
            }

            if ($weakAssertionCount -gt 0 -and ($weakAssertionCount / [math]::Max(1, $assertionCount)) -ge 0.6) {
                $score += 15
                $reasons.Add('mostly-shape-or-boolean-assertions')
            }

            if ($substituteCount -ge 8 -and $substituteCount -gt $assertionCount) {
                $score += 20
                $reasons.Add('mock-heavy-relative-to-assertions')
            }
            elseif ($substituteCount -ge 12) {
                $score += 10
                $reasons.Add('mock-heavy')
            }

            if ($reflectionCount -ge 5) {
                $score += 20
                $reasons.Add('reflection-shape-test')
            }

            if ($diCount -ge 3) {
                $score += 12
                $reasons.Add('di-wiring-test')
            }

            if ($snapshotCount -ge 2) {
                $score += 10
                $reasons.Add('snapshot-or-string-stability-test')
            }

            if ($lineCount -ge 700) {
                $score += 18
                $reasons.Add('very-large-test-file')
            }
            elseif ($lineCount -ge 400) {
                $score += 8
                $reasons.Add('large-test-file')
            }

            if ($linesPerTest -ge 35) {
                $score += 12
                $reasons.Add('high-lines-per-test')
            }

            if ($testCount -ge 50) {
                $score += 10
                $reasons.Add('many-test-methods')
            }
        }
    }

    if ($category -eq 'Architecture' -and -not $isPolicyRatchet) {
        $score += 8
        $reasons.Add('architecture-test-review-for-policy-value')
    }

    if ($testCount -gt 0 -and $productionMatches.Count -eq 0 -and $category -notin @('Architecture', 'Infrastructure', 'ViewComponents', 'ViewModels')) {
        $score += 8
        $reasons.Add('no-obvious-production-file-match')
    }

    $recommendation = if ($score -ge 45) {
        'Review for deletion/consolidation'
    }
    elseif ($score -ge 25) {
        'Review for simplification'
    }
    elseif ($score -ge 10) {
        'Keep unless touched'
    }
    else {
        'Likely useful'
    }

    [pscustomobject]@{
        Path = $repoPath
        Category = $category
        Subject = $subject
        ProductionMatches = $productionMatches
        Lines = $lineCount
        Classes = $classCount
        Tests = $testCount
        Assertions = $assertionCount
        WeakAssertions = $weakAssertionCount
        Substitutes = $substituteCount
        ReflectionUses = $reflectionCount
        DiUses = $diCount
        IsDiCycleSafetyNet = $isDiCycleSafetyNet
        SnapshotUses = $snapshotCount
        RatchetUses = $ratchetCount
        AssertionsPerTest = if ($testCount -gt 0) { [math]::Round($assertionCount / $testCount, 2) } else { 0 }
        LinesPerTest = if ($testCount -gt 0) { [math]::Round($lineCount / $testCount, 1) } else { 0 }
        DebtScore = $score
        Recommendation = $recommendation
        Reasons = @($reasons)
    }
}

$outDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $Root $OutputDirectory
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $outDir "test-utility-$timestamp.json"
$csvPath = Join-Path $outDir "test-utility-$timestamp.csv"
$mdPath = Join-Path $outDir "test-utility-$timestamp.md"

$rowsSorted = @($rows | Sort-Object DebtScore, Lines -Descending)
$rowsSorted | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding utf8
$rowsSorted |
    Select-Object Path,Category,Lines,Tests,Assertions,AssertionsPerTest,LinesPerTest,Substitutes,ReflectionUses,DiUses,DebtScore,Recommendation,@{Name='Reasons'; Expression={ $_.Reasons -join '; ' }} |
    Export-Csv -NoTypeInformation -Path $csvPath -Encoding utf8

$classKeys = foreach ($row in $rows) {
    $fullPath = Join-Path $Root $row.Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $fullPath)) { continue }
    $text = Get-Content -Raw -Path $fullPath
    $ns = [regex]::Match($text, 'namespace\s+([^;{]+)').Groups[1].Value
    foreach ($match in [regex]::Matches($text, '\b(public\s+)?(sealed\s+|static\s+|abstract\s+|partial\s+)*class\s+(\w+)')) {
        "$ns.$($match.Groups[3].Value)"
    }
}

$distinctClassCount = @($classKeys | Select-Object -Unique).Count
$classDeclarationCount = @($classKeys).Count

$summaryByCategory = $rows |
    Group-Object Category |
    Sort-Object Name |
    ForEach-Object {
        $group = $_.Group
        [pscustomobject]@{
            Category = $_.Name
            Files = $group.Count
            Lines = ($group | Measure-Object Lines -Sum).Sum
            Tests = ($group | Measure-Object Tests -Sum).Sum
            Assertions = ($group | Measure-Object Assertions -Sum).Sum
            ReviewFiles = @($group | Where-Object { $_.DebtScore -ge 25 }).Count
        }
    }

$md = New-Object System.Collections.Generic.List[string]
$md.Add('# Test Utility Analysis')
$md.Add('')
$md.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$md.Add('')
$md.Add('This is a heuristic review queue, not an automated verdict. High scores mean a test file has signals often associated with maintenance cost: low assertion density, reflection/DI shape checks, heavy mocks, large size, or no obvious production subject.')
$md.Add('')
$md.Add('## Inventory')
$md.Add('')
$md.Add(('- Test files: {0}' -f $rows.Count))
$md.Add(('- Test class declarations: {0}' -f $classDeclarationCount))
$md.Add(('- Distinct test class types: {0}' -f $distinctClassCount))
$md.Add(('- Test attributes: {0}' -f (($rows | Measure-Object Tests -Sum).Sum)))
$md.Add(('- Test lines: {0}' -f (($rows | Measure-Object Lines -Sum).Sum)))
$md.Add('')
$md.Add('## Summary By Category')
$md.Add('')
$md.Add('| Category | Files | Lines | Tests | Assertions | Review Files |')
$md.Add('| --- | ---: | ---: | ---: | ---: | ---: |')
foreach ($item in $summaryByCategory) {
    $md.Add("| $($item.Category) | $($item.Files) | $($item.Lines) | $($item.Tests) | $($item.Assertions) | $($item.ReviewFiles) |")
}
$md.Add('')
$md.Add("## Top $Top Review Candidates")
$md.Add('')
$md.Add('| Score | File | Tests | Assertions/Test | Lines/Test | Reasons |')
$md.Add('| ---: | --- | ---: | ---: | ---: | --- |')
foreach ($item in ($rowsSorted | Select-Object -First $Top)) {
    $md.Add(('| {0} | `{1}` | {2} | {3} | {4} | {5} |' -f $item.DebtScore, $item.Path, $item.Tests, $item.AssertionsPerTest, $item.LinesPerTest, ($item.Reasons -join ', ')))
}

$highConfidence = @($rowsSorted |
    Where-Object {
        $_.DebtScore -ge 35 -and
        $_.Category -ne 'Architecture' -and
        -not $_.IsDiCycleSafetyNet -and
        ($_.Reasons -notcontains 'helper-or-fixture-no-tests') -and
        ($_.Reasons -notcontains 'policy-ratchet-review-for-consolidation')
    } |
    Select-Object -First $Top)

$md.Add('')
$md.Add('## High-Confidence Test-Debt Candidates')
$md.Add('')
$md.Add('These are non-architecture test files with multiple maintenance-cost signals. They are the best first review queue for deletion, consolidation, or replacement with a narrower behavior test.')
$md.Add('')
$md.Add('| Score | File | Tests | Assertions/Test | Lines/Test | Reasons |')
$md.Add('| ---: | --- | ---: | ---: | ---: | --- |')
foreach ($item in $highConfidence) {
    $md.Add(('| {0} | `{1}` | {2} | {3} | {4} | {5} |' -f $item.DebtScore, $item.Path, $item.Tests, $item.AssertionsPerTest, $item.LinesPerTest, ($item.Reasons -join ', ')))
}

if (-not [string]::IsNullOrWhiteSpace($StrykerReport)) {
    $strykerPath = if ([System.IO.Path]::IsPathRooted($StrykerReport)) { $StrykerReport } else { Join-Path $Root $StrykerReport }
    if (Test-Path $strykerPath) {
        $report = Get-Content -Raw -Path $strykerPath | ConvertFrom-Json
        $mutants = foreach ($prop in $report.files.PSObject.Properties) {
            foreach ($mutant in $prop.Value.mutants) {
                [pscustomobject]@{
                    File = Convert-ToRepoPath $prop.Name
                    Status = $mutant.status
                }
            }
        }
        $sourceRows = $mutants |
            Group-Object File |
            ForEach-Object {
                $group = $_.Group
                $killed = @($group | Where-Object Status -eq 'Killed').Count
                $survived = @($group | Where-Object Status -eq 'Survived').Count
                $tested = $killed + $survived
                [pscustomobject]@{
                    File = $_.Name
                    Killed = $killed
                    Survived = $survived
                    Tested = $tested
                    Score = if ($tested -gt 0) { [math]::Round(($killed / $tested) * 100, 2) } else { $null }
                }
            } |
            Where-Object { $_.Tested -gt 0 } |
            Sort-Object Score, Tested

        $md.Add('')
        $md.Add('## Stryker Weakest Source Areas')
        $md.Add('')
        $md.Add(('Report: `{0}`' -f $StrykerReport))
        $md.Add('')
        $md.Add('| Mutation Score | Source File | Tested Mutants | Killed | Survived |')
        $md.Add('| ---: | --- | ---: | ---: | ---: |')
        foreach ($item in ($sourceRows | Select-Object -First 30)) {
            $md.Add(('| {0}% | `{1}` | {2} | {3} | {4} |' -f $item.Score, $item.File, $item.Tested, $item.Killed, $item.Survived))
        }
    }
}

$md | Set-Content -Path $mdPath -Encoding utf8

Write-Host "Wrote:"
Write-Host "  $jsonPath"
Write-Host "  $csvPath"
Write-Host "  $mdPath"
Write-Host ''
Write-Host "Top $Top candidates:"
$rowsSorted |
    Select-Object -First $Top Path,DebtScore,Tests,AssertionsPerTest,LinesPerTest,Recommendation,@{Name='Reasons'; Expression={ $_.Reasons -join '; ' }} |
    Format-Table -AutoSize
