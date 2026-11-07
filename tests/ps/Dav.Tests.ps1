<#
.SYNOPSIS
    End-to-end tests of the Dav module in the PowerShell that runs this file (PowerShell 7 or Windows PowerShell 5.1).

.DESCRIPTION
    Every case starts a child process of the same PowerShell, imports the module and checks the output and the real process
    exit code, because the exit code is what a CI step sees. No test framework is needed.

.EXAMPLE
    pwsh -File tests/ps/Dav.Tests.ps1 -Module artifacts/Dav -Samples tests/DotNetVisualizer.Web.Tests/bin/Debug/net8.0/samples
    powershell -File tests/ps/Dav.Tests.ps1 -Module artifacts/Dav -Samples tests/DotNetVisualizer.Web.Tests/bin/Debug/net8.0/samples
#>
param(
    [Parameter(Mandatory)] [string] $Module,
    [Parameter(Mandatory)] [string] $Samples
)

$ErrorActionPreference = 'Stop'
$Module = (Resolve-Path $Module).Path
$Samples = (Resolve-Path $Samples).Path
$manifest = Join-Path $Module 'Dav.psd1'
$hostExe = if ($PSVersionTable.PSEdition -eq 'Core') { Join-Path $PSHOME ($(if ($IsWindows -or $PSVersionTable.PSVersion.Major -lt 6) { 'pwsh.exe' } else { 'pwsh' })) } else { Join-Path $PSHOME 'powershell.exe' }
$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows
$policy = if ($onWindows) { '-ExecutionPolicy Bypass ' } else { '' }   # the execution policy exists on Windows only
$work = Join-Path ([IO.Path]::GetTempPath()) ('dav-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null

$script:passed = 0
$script:failed = @()

# Runs a script block's text in a fresh process of this PowerShell; returns the exit code and everything it printed.
# Mode Stop: the script sets $ErrorActionPreference = Stop (what GitHub Actions does for pwsh); Plain: it does not; Command: pwsh -Command "<the statements>" (the way a CI step runs a command).
function Invoke-Child([string] $Body, [string] $Dir = $work, [string] $Mode = 'Stop') {
    $file = Join-Path $work ('case-' + [guid]::NewGuid().ToString('N') + '.ps1')
    $text = "Import-Module '$manifest' -Force`n" + $(if ($Mode -eq 'Stop') { "`$ErrorActionPreference = 'Stop'`n" } else { '' }) + $Body
    [IO.File]::WriteAllText($file, $text, (New-Object Text.UTF8Encoding $true))
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $hostExe
    $psi.Arguments = if ($Mode -eq 'Command') { "-NoProfile -NonInteractive $policy-Command `"Import-Module '$manifest'; $($Body.Replace("`r", '').Replace("`n", '; '))`"" } else { "-NoProfile -NonInteractive $policy-File `"$file`"" }
    $psi.WorkingDirectory = $Dir
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $stdout.Result; Err = $stderr.Result; All = $stdout.Result + $stderr.Result }
}

function Test-Case([string] $Name, [scriptblock] $Check) {
    try {
        & $Check
        $script:passed++
        Write-Host "  ok   $Name"
    }
    catch {
        $script:failed += $Name
        Write-Host "  FAIL $Name" -ForegroundColor Red
        Write-Host "       $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Assert-Exit($r, [int] $Expected) { Assert ($r.Exit -eq $Expected) "exit code was $($r.Exit), expected $Expected. Output:`n$($r.All)" }
function Assert-NonZero($r) { Assert ($r.Exit -ne 0) "exit code was 0, expected a failure. Output:`n$($r.All)" }
function Assert-Has($r, [string] $Text) { Assert ($r.All.Contains($Text)) "output does not contain '$Text'. Output:`n$($r.All)" }
function Assert-Lacks($r, [string] $Text) { Assert (-not $r.All.Contains($Text)) "output must not contain '$Text'. Output:`n$($r.All)" }

$risky = Join-Path $Samples 'RiskyApis.dll'
$cycles = Join-Path $Samples 'CircularDependencies.dll'
$clean = Join-Path $Samples 'Inheritance.dll'
$v1 = Join-Path $Samples 'api-compat/v1/ApiCompat.dll'
$v2 = Join-Path $Samples 'api-compat/v2/ApiCompat.dll'

Write-Host "Dav module tests in $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion) ($hostExe)"

# the rule ids of the risky sample by level, from the module itself
$ids = Invoke-Child "try { Get-DavRiskyApi '$risky' -ErrorAction Stop | ForEach-Object { `"`$(`$_.Severity)=`$(`$_.RuleId)`" } } catch { }"
$byLevel = @{ High = @(); Medium = @(); Low = @() }
foreach ($line in ($ids.Out -split "`r?`n")) { if ($line -match '^(High|Medium|Low)=(.+)$') { $byLevel[$Matches[1]] += $Matches[2] } }
$allHigh = ($byLevel.High | Select-Object -Unique) -join "','"
$allMedium = ($byLevel.Medium | Select-Object -Unique) -join "','"
Test-Case 'the risky sample has high, medium and low leads' { Assert ($byLevel.High.Count -gt 0 -and $byLevel.Medium.Count -gt 0 -and $byLevel.Low.Count -gt 0) "levels: High=$($byLevel.High.Count) Medium=$($byLevel.Medium.Count) Low=$($byLevel.Low.Count)" }

# --- risk threshold -------------------------------------------------------------------------------------------------
Test-Case 'risky APIs fail the command by default (exit 1) and are still listed' {
    $r = Invoke-Child "Get-DavRiskyApi '$risky'"
    Assert-Exit $r 1; Assert-Has $r 'deserialize-binaryformatter'; Assert-Has $r 'risky API lead'
}
Test-Case 'threshold high: a high lead fails' { Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky' -FailOn high") 1 }
Test-Case 'threshold high: medium and low leads do not fail (the high ones are accepted)' {
    Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky' -FailOn high -Allow '$allHigh'") 0
}
Test-Case 'threshold medium: a medium lead fails when the high ones are accepted' {
    Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky' -FailOn medium -Allow '$allHigh'") 1
}
Test-Case 'threshold medium: a low lead does not fail when high and medium are accepted (exact threshold, below threshold)' {
    Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky' -FailOn medium -Allow '$allHigh','$allMedium'") 0
}
Test-Case 'threshold low: a low lead fails when high and medium are accepted' {
    Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky' -FailOn low -Allow '$allHigh','$allMedium'") 1
}
Test-Case 'the alias -RiskThreshold works like -FailOn' { Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky' -RiskThreshold high -Allow '$allHigh'") 0 }
Test-Case 'an unsupported threshold is an argument error that names the allowed values' {
    $r = Invoke-Child "Get-DavRiskyApi '$risky' -FailOn severe"
    Assert-NonZero $r; Assert-Has $r 'low'; Assert-Has $r 'medium'; Assert-Has $r 'high'
}
Test-Case 'an assembly without risky calls passes quietly' {
    $r = Invoke-Child "Get-DavRiskyApi '$clean'"
    Assert-Exit $r 0; Assert-Lacks $r 'RuleId'
}
Test-Case 'without the flag the behaviour is the same as -FailOn low' {
    $a = Invoke-Child "Get-DavRiskyApi '$risky' -Allow '$allHigh','$allMedium'"
    $b = Invoke-Child "Get-DavRiskyApi '$risky' -FailOn low -Allow '$allHigh','$allMedium'"
    Assert ($a.Exit -eq $b.Exit -and $a.Exit -eq 1) "exit codes: $($a.Exit) and $($b.Exit)"
}
Test-Case 'a dav.json next to the working directory accepts rules, with a reason' {
    $dir = Join-Path $work 'cfg'; New-Item -ItemType Directory $dir | Out-Null
    $rules = @($byLevel.High + $byLevel.Medium + $byLevel.Low | Select-Object -Unique | ForEach-Object { @{ id = $_; reason = 'accepted in the test' } })
    Set-Content (Join-Path $dir 'dav.json') (ConvertTo-Json @{ risky = @{ allow = $rules } } -Depth 6) -Encoding UTF8
    Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky'" $dir) 0
}
Test-Case 'a broken dav.json is an error, not a silent pass' {
    $dir = Join-Path $work 'badcfg'; New-Item -ItemType Directory $dir | Out-Null
    Set-Content (Join-Path $dir 'dav.json') '{ not json' -Encoding UTF8
    Assert-NonZero (Invoke-Child "Get-DavRiskyApi '$clean'" $dir)
}

# --- how the failure reaches the process exit code -------------------------------------------------------------------
Test-Case 'pwsh -Command (how CI shells run a step): a failed check is exit 1' { Assert-Exit (Invoke-Child "Get-DavRiskyApi '$risky'" $work 'Command') 1 }
Test-Case 'pwsh -File with -ExitOnFailure: a failed check is exit 1 without setting $ErrorActionPreference' {
    $r = Invoke-Child "Get-DavRiskyApi '$risky' -ExitOnFailure" $work 'Plain'
    Assert-Exit $r 1; Assert-Has $r 'risky API lead'
}
Test-Case '-ExitOnFailure also ends an unreadable input with exit 1' { Assert-Exit (Invoke-Child "Get-DavRiskyApi '$Samples/nope.dll' -ExitOnFailure" $work 'Plain') 1 }
Test-Case '-ExitOnFailure does not end a passing run with an error' { Assert-Exit (Invoke-Child "Get-DavRiskyApi '$clean' -ExitOnFailure" $work 'Plain') 0 }

# --- circular dependencies: warnings only ----------------------------------------------------------------------------
Test-Case 'circular dependencies warn with class and method names and do not fail' {
    $r = Invoke-Child "Get-DavCircularDependency '$cycles'"
    Assert-Exit $r 0
    Assert-Has $r '[WARNING] Possible Circular Dependency Detected'
    Assert-Has $r 'Classes:'; Assert-Has $r 'Methods:'
    Assert-Has $r 'Samples.Cycles.SimpleCycle'; Assert-Has $r 'Samples.Cycles.SimpleCycle.A()'
    Assert-Has $r 'Warning: These classes and methods contain a possible circular dependency.'
}
Test-Case 'each cycle is warned about once' {
    $r = Invoke-Child "(Get-DavCircularDependency '$cycles' -WarningAction SilentlyContinue | Measure-Object).Count; (Get-DavCircularDependency '$cycles' -WarningAction SilentlyContinue | ForEach-Object { (`$_.Methods | Sort-Object) -join '|' } | Sort-Object -Unique | Measure-Object).Count"
    $n = ($r.Out -split "`r?`n" | Where-Object { $_ -match '^\d+$' })
    Assert ($n.Count -eq 2 -and $n[0] -eq $n[1] -and [int]$n[0] -ge 5) "counts were: $($n -join ', ')"
}
Test-Case 'no cycles: no warning section' {
    $r = Invoke-Child "Get-DavCircularDependency '$clean'"
    Assert-Exit $r 0; Assert-Lacks $r 'WARNING'; Assert-Lacks $r 'Classes:'
}
Test-Case 'the check command warns about cycles and still passes (vulnerability lookup skipped)' {
    $r = Invoke-Child "Invoke-DavCheck '$cycles' -SkipVulnerabilities"
    Assert-Exit $r 0; Assert-Has $r 'Possible Circular Dependency Detected'
}
Test-Case 'a failed risk check and cycle warnings together: the command fails and the warnings are still shown' {
    $r = Invoke-Child "Invoke-DavCheck '$cycles','$risky' -SkipVulnerabilities -FailOn high"
    Assert-Exit $r 1; Assert-Has $r 'Possible Circular Dependency Detected'; Assert-Has $r 'Check failed'
}
Test-Case 'the check command with only accepted risks passes' {
    Assert-Exit (Invoke-Child "Invoke-DavCheck '$risky' -SkipVulnerabilities -FailOn high -Allow '$allHigh'") 0
}

# --- compare versions ------------------------------------------------------------------------------------------------
Test-Case 'the same file compared with itself: identical, passes, fingerprints equal' {
    $r = Invoke-Child "Compare-DavVersion '$v1' '$v1' | Format-List Verdict,VersionMismatch,OldFileHash,NewFileHash"
    Assert-Exit $r 0; Assert-Has $r 'identical file'
}
Test-Case 'a major bump that covers the breaking changes passes and keeps the comparison result' {
    $r = Invoke-Child "Compare-DavVersion '$v1' '$v2' | Format-List Verdict,RequiredBump,ActualBump,VersionMismatch,Breaking"
    Assert-Exit $r 0; Assert-Has $r 'public API changed'; Assert-Has $r 'VersionMismatch : False'
}
Test-Case 'a version number too small for the changes fails (exit 1) and the result is still printed' {
    # the newer build compared as the old one: the same removals now need a major bump that the version number does not show
    $r = Invoke-Child "Compare-DavVersion '$v2' '$v1' | Format-List Verdict,RequiredBump,ActualBump,VersionMismatch"
    Assert-Exit $r 1; Assert-Has $r 'VersionMismatch : True'; Assert-Has $r 'Version mismatch'; Assert-Has $r 'major'
}
Test-Case 'threshold high still fails for a missing major bump' { Assert-Exit (Invoke-Child "Compare-DavVersion '$v2' '$v1' -FailOn high") 1 }
Test-Case 'a missing file is an error' { Assert-NonZero (Invoke-Child "Compare-DavVersion '$v1' '$Samples/nope.dll'") }

# --- Get-Help --------------------------------------------------------------------------------------------------------
Test-Case 'every cmdlet has help: a synopsis, a description, described parameters and examples' {
    $r = Invoke-Child "foreach (`$c in (Get-Command -Module Dav)) { `$h = Get-Help `$c.Name -Full; `"`$(`$c.Name)|`$(`$h.Synopsis.StartsWith(`$c.Name))|`$(@(`$h.examples.example).Count)|`$(@(`$h.parameters.parameter | Where-Object { -not `$_.description }).Count)|`$(`$h.description.Text.Length -gt 40)`" }"
    $rows = @($r.Out -split "`r?`n" | Where-Object { $_ -match '\|' })
    Assert ($rows.Count -eq 5) "expected 5 cmdlets, got $($rows.Count). Output:`n$($r.All)"
    foreach ($row in $rows) {
        $f = $row -split '\|'
        Assert ($f[1] -eq 'False') "$($f[0]) has no synopsis (Get-Help shows only the syntax)"
        Assert ([int]$f[2] -ge 2) "$($f[0]) has fewer than 2 examples"
        Assert ($f[3] -eq '0') "$($f[0]) has undescribed parameters"
        Assert ($f[4] -eq 'True') "$($f[0]) has no description"
    }
}
Test-Case 'the about_Dav topic is found' {
    $r = Invoke-Child "(Get-Help about_Dav | Out-String)"
    Assert-Has $r 'THE RISK THRESHOLD'
}

# --- inputs and vulnerabilities --------------------------------------------------------------------------------------
Test-Case 'a missing assembly is an error' { Assert-NonZero (Invoke-Child "Get-DavRiskyApi '$Samples/nope.dll'") }
Test-Case 'a file that is not a .NET assembly is an error' {
    $junk = Join-Path $work 'junk.dll'; [IO.File]::WriteAllBytes($junk, [byte[]](1..64))
    Assert-NonZero (Invoke-Child "Get-DavCircularDependency '$junk'")
}
Test-Case 'a vulnerability lookup that cannot finish is not a clean pass (exit 1, INCOMPLETE)' {
    $r = Invoke-Child "`$env:VULN_OSV_URL='http://127.0.0.1:9/'; `$env:VULN_NUGET_URL='http://127.0.0.1:9/'; Get-DavVulnerability '$(Join-Path $Samples 'VulnerableDemo.dll')'"
    Assert-Exit $r 1; Assert-Has $r 'INCOMPLETE'
}
Test-Case '-AllowIncomplete accepts an unfinished lookup that found nothing to fail on' {
    $r = Invoke-Child "`$env:VULN_OSV_URL='http://127.0.0.1:9/'; `$env:VULN_NUGET_URL='http://127.0.0.1:9/'; Get-DavVulnerability '$(Join-Path $Samples 'VulnerableDemo.dll')' -AllowIncomplete"
    Assert-Exit $r 0
}
Test-Case 'the module works the same for every cmdlet name it exports' {
    $r = Invoke-Child "(Get-Command -Module Dav).Name | Sort-Object"
    foreach ($n in 'Compare-DavVersion', 'Get-DavCircularDependency', 'Get-DavRiskyApi', 'Get-DavVulnerability', 'Invoke-DavCheck') { Assert-Has $r $n }
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "$script:passed passed, $($script:failed.Count) failed"
if ($script:failed.Count -gt 0) { $script:failed | ForEach-Object { Write-Host "  failed: $_" -ForegroundColor Red }; exit 1 }
exit 0
