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
    $assertionCount = Count-Matches $text '(\.Should\(|\bAssert\.|\b(?:Received|DidNotReceive)(?:WithAnyArgs)?\(|\bVerify\(|\bMustHaveHappened)'
    $weakAssertionCount = Count-Matches $text '(\.Should\(\)\.(NotBeNull|BeNull|BeOfType|BeAssignableTo|NotThrow|BeTrue|BeFalse)\b|\bAssert\.(NotNull|Null|IsType|IsAssignableFrom|True|False)\b)'
    $substituteCount = Count-Matches $text '\bSubstitute\.For<|\bMock<|\bNSubstitute\.'
    $distinctMockTypes = @([regex]::Matches($text, '\bSubstitute\.For<([^>(]+(?:<[^>]+>)?)') |
        ForEach-Object { $_.Groups[1].Value.Trim() } |
        Sort-Object -Unique).Count
    $isCoordinatorSut = $distinctMockTypes -ge 8
    $reflectionCount = Count-Matches $text '\btypeof\(|\.Get(Constructor|Method|Property|Properties|Interfaces|GenericArguments|CustomAttributes)|\.Namespace\b|\.Assembly\b'
    $diCount = Count-Matches $text '\bServiceCollection\b|\bBuildServiceProvider\b|\bGetRequiredService\b|\bIServiceCollection\b'
    $snapshotCount = Count-Matches $text '\bEnum\.GetValues\b|\.ToString\(\)\.Should\(\)\.Be\(|\.Should\(\)\.BeEquivalentTo\(new\[\]'
    $ratchetCount = Count-Matches $text '\bRatchetTestRunner\.Run\('
    $assertionCount += $ratchetCount
    $subject = Get-TestSubjectName $repoPath
    $productionMatches = Find-ProductionMatches $subject
    $category = Get-Category $repoPath

    $publicMethodNames = @()
    $untestedPublicMethods = @()
    $testsPerPublicMethod = 0.0
    $maxTestsPerMethod = 0
    $topMethodsByTestCount = ''
    if ($productionMatches.Count -gt 0) {
        $methodSet = New-Object System.Collections.Generic.HashSet[string]
        foreach ($prodRepoPath in $productionMatches) {
            $prodFullPath = Join-Path $Root $prodRepoPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path $prodFullPath)) { continue }
            $prodText = Get-Content -Raw -Path $prodFullPath
            $prodClassName = [System.IO.Path]::GetFileNameWithoutExtension($prodFullPath)
            $methodMatches = [regex]::Matches($prodText,
                '(?m)^\s+public\s+(?:(?:async|virtual|override|static|sealed|new|unsafe|extern|partial)\s+)*(?:(?!class\b|interface\b|enum\b|struct\b|record\b)[\w<>?,\.\[\]]+\s+)+(\w+)\s*\(')
            foreach ($mm in $methodMatches) {
                $methodName = $mm.Groups[1].Value
                if ($methodName -eq $prodClassName) { continue }
                [void]$methodSet.Add($methodName)
            }
        }
        $publicMethodNames = @($methodSet | Sort-Object)
        if ($publicMethodNames.Count -gt 0 -and $testCount -gt 0) {
            $testNameMatches = [regex]::Matches($text,
                '(?m)^\s+public\s+(?:async\s+)?(?:Task<[^>]+>|Task|void|ValueTask|ValueTask<[^>]+>)\s+(\w+)\s*\(')
            $testedPrefixes = New-Object System.Collections.Generic.HashSet[string]
            $prefixCounts = @{}
            foreach ($tm in $testNameMatches) {
                $tn = $tm.Groups[1].Value
                $idx = $tn.IndexOf('_')
                $prefix = if ($idx -gt 0) { $tn.Substring(0, $idx) } else { $tn }
                [void]$testedPrefixes.Add($prefix)
                if ($prefixCounts.ContainsKey($prefix)) { $prefixCounts[$prefix]++ } else { $prefixCounts[$prefix] = 1 }
            }
            $methodMatchedCounts = @{}
            $publicMethodSet = New-Object System.Collections.Generic.HashSet[string]
            foreach ($pmName in $publicMethodNames) { [void]$publicMethodSet.Add($pmName) }
            foreach ($pmName in $publicMethodNames) {
                $count = 0
                if ($prefixCounts.ContainsKey($pmName)) { $count += $prefixCounts[$pmName] }
                if ($pmName.EndsWith('Async', [StringComparison]::Ordinal)) {
                    $stripped = $pmName.Substring(0, $pmName.Length - 5)
                    # Only attribute the stripped (sync-shaped) prefix to this async method
                    # when there is no sync sibling on the same SUT — otherwise the sync
                    # method already claims those tests via the exact-match branch above.
                    if (-not $publicMethodSet.Contains($stripped) -and $prefixCounts.ContainsKey($stripped)) {
                        $count += $prefixCounts[$stripped]
                    }
                }
                if ($count -gt 0) { $methodMatchedCounts[$pmName] = $count }
            }
            if ($methodMatchedCounts.Count -gt 0) {
                $maxTestsPerMethod = ($methodMatchedCounts.Values | Measure-Object -Maximum).Maximum
                $topMethodsByTestCount = (($methodMatchedCounts.GetEnumerator() |
                    Sort-Object Value -Descending |
                    Select-Object -First 3 |
                    ForEach-Object { "$($_.Key)($($_.Value))" }) -join ', ')
            }
            $untestedPublicMethods = @($publicMethodNames | Where-Object {
                $name = $_
                $stripped = if ($name.EndsWith('Async', [StringComparison]::Ordinal)) {
                    $name.Substring(0, $name.Length - 5)
                } else { $null }
                -not $testedPrefixes.Contains($name) -and -not ($stripped -and $testedPrefixes.Contains($stripped))
            })
            $testsPerPublicMethod = [math]::Round($testCount / $publicMethodNames.Count, 2)
        }
    }
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

            if ($isCoordinatorSut) {
                $reasons.Add("coordinator-sut-$distinctMockTypes-mocks")
            }
            elseif ($substituteCount -ge 8 -and $substituteCount -gt $assertionCount) {
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

            if ($linesPerTest -ge 35 -and -not $isCoordinatorSut) {
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
        DistinctMockTypes = $distinctMockTypes
        IsCoordinatorSut = $isCoordinatorSut
        ReflectionUses = $reflectionCount
        DiUses = $diCount
        IsDiCycleSafetyNet = $isDiCycleSafetyNet
        SnapshotUses = $snapshotCount
        RatchetUses = $ratchetCount
        AssertionsPerTest = if ($testCount -gt 0) { [math]::Round($assertionCount / $testCount, 2) } else { 0 }
        LinesPerTest = if ($testCount -gt 0) { [math]::Round($lineCount / $testCount, 1) } else { 0 }
        PublicMethodCount = $publicMethodNames.Count
        UntestedPublicMethods = $untestedPublicMethods
        TestsPerPublicMethod = $testsPerPublicMethod
        MaxTestsPerMethod = $maxTestsPerMethod
        TopMethodsByTestCount = $topMethodsByTestCount
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

$gapCandidates = @($rows |
    Where-Object {
        $_.PublicMethodCount -ge 3 -and
        $_.UntestedPublicMethods.Count -ge 1 -and
        $_.Category -ne 'Architecture' -and
        -not $_.IsDiCycleSafetyNet
    } |
    Sort-Object @{Expression = { $_.UntestedPublicMethods.Count }; Descending = $true }, @{Expression = { $_.TestsPerPublicMethod } } |
    Select-Object -First $Top)

$md.Add('')
$md.Add('## Coverage Gap Candidates')
$md.Add('')
$md.Add('Test files where the SUT''s public surface has methods with no direct test (test name does not start with `MethodName_`). Attribution is conservative — tests that exercise a method indirectly are not credited. Use this list to find missing tests, not bloated ones.')
$md.Add('')
$md.Add('| Untested | Tests/Method | File | Public Methods | Untested Method Names |')
$md.Add('| ---: | ---: | --- | ---: | --- |')
foreach ($item in $gapCandidates) {
    $untestedNames = ($item.UntestedPublicMethods | Select-Object -First 8) -join ', '
    if ($item.UntestedPublicMethods.Count -gt 8) {
        $untestedNames += ", ... +$($item.UntestedPublicMethods.Count - 8) more"
    }
    $md.Add(('| {0} | {1} | `{2}` | {3} | {4} |' -f $item.UntestedPublicMethods.Count, $item.TestsPerPublicMethod, $item.Path, $item.PublicMethodCount, $untestedNames))
}

$concentrationCandidates = @($rows |
    Where-Object {
        $_.MaxTestsPerMethod -ge 5 -and
        $_.Category -ne 'Architecture' -and
        -not $_.IsDiCycleSafetyNet
    } |
    Sort-Object @{Expression = { $_.MaxTestsPerMethod }; Descending = $true }, @{Expression = { $_.Tests }; Descending = $true } |
    Select-Object -First $Top)

$md.Add('')
$md.Add('## Test Concentration Candidates')
$md.Add('')
$md.Add('Files where a single SUT method has 5+ attributed tests. High concentration often means cosmetic-variation tests that could collapse to a `[Theory]` or be culled outright. Inspect the top method''s tests — if 8 of them differ only in input values with the same assertion shape, that''s a consolidation target.')
$md.Add('')
$md.Add('| Max Tests/Method | Tests | File | Top Methods (test count) |')
$md.Add('| ---: | ---: | --- | --- |')
foreach ($item in $concentrationCandidates) {
    $md.Add(('| {0} | {1} | `{2}` | {3} |' -f $item.MaxTestsPerMethod, $item.Tests, $item.Path, $item.TopMethodsByTestCount))
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
