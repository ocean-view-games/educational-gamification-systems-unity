<#
.SYNOPSIS
    Runs this package's Play Mode suite headlessly, the way a consuming project sees it.

.DESCRIPTION
    Assembles a throwaway Unity project that installs this package as a dependency,
    copies the samples into it so they are compiled too, runs the Play Mode tests in
    batch mode, and reports the result.

    Testing through a consuming project rather than embedded source is deliberate: it
    exercises the package the way a studio actually receives it, and it catches a
    sample drifting out of sync with the runtime API, which nothing else checks.

    The scratch project is reused between runs so Unity's Library cache survives and
    later runs are much faster. Pass -Clean to start from scratch.

.PARAMETER UnityVersion
    Editor version to use, e.g. 6000.0.73f1. Defaults to the newest installed under
    the Unity Hub editor directory.

.PARAMETER ProjectPath
    Where to build the scratch consuming project. Defaults to a stable directory in
    the user's temp folder.

.PARAMETER Clean
    Delete the scratch project before running, forcing a full reimport.

.EXAMPLE
    .\verify.ps1

.EXAMPLE
    .\verify.ps1 -UnityVersion 6000.0.73f1 -Clean
#>

[CmdletBinding()]
param(
    [string] $UnityVersion,
    [string] $ProjectPath,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

$packageRoot = Split-Path -Parent $PSScriptRoot
$packageJsonPath = Join-Path $packageRoot 'package.json'

if (-not (Test-Path $packageJsonPath)) {
    throw "package.json not found at $packageJsonPath. Run this script from the package's Tools~ folder."
}

$package = Get-Content $packageJsonPath -Raw | ConvertFrom-Json
Write-Host "Package : $($package.name) $($package.version)"

# -- Locate an editor ---------------------------------------------------------

$hubEditorRoot = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'

if ([string]::IsNullOrWhiteSpace($UnityVersion)) {
    if (-not (Test-Path $hubEditorRoot)) {
        throw "No Unity Hub editor directory at $hubEditorRoot. Pass -UnityVersion with an explicit version."
    }

    # Newest by name. Unity version strings sort correctly for this purpose.
    # Wrapped in @() so a single install, or none, still counts correctly.
    $installed = @(Get-ChildItem $hubEditorRoot -Directory | Sort-Object Name -Descending)

    if ($installed.Count -eq 0) {
        throw "No editors installed under $hubEditorRoot."
    }

    $UnityVersion = $installed[0].Name
}

$unityExe = Join-Path $hubEditorRoot "$UnityVersion\Editor\Unity.exe"

if (-not (Test-Path $unityExe)) {
    throw "Unity $UnityVersion not found at $unityExe."
}

Write-Host "Editor  : $UnityVersion"

# -- Assemble the consuming project -------------------------------------------

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $env:TEMP 'ovg-edtech-verify'
}

if ($Clean -and (Test-Path $ProjectPath)) {
    Write-Host "Cleaning $ProjectPath"
    Remove-Item $ProjectPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $ProjectPath 'Packages') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ProjectPath 'Assets') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ProjectPath 'ProjectSettings') | Out-Null

# Unity wants forward slashes in a file: dependency.
$packageUri = ($packageRoot -replace '\\', '/')

$manifest = @"
{
  "dependencies": {
    "$($package.name)": "file:$packageUri",
    "com.unity.test-framework": "1.4.5",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0"
  },
  "testables": [
    "$($package.name)"
  ]
}
"@

# Written without a byte order mark on purpose. Windows PowerShell's -Encoding utf8
# emits one, and Unity's manifest parser rejects the file outright as "Non-whitespace
# before {[" with no mention of a BOM, which is a thoroughly unhelpful way to spend
# an afternoon.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $ProjectPath 'Packages\manifest.json'), $manifest, $utf8NoBom)

# Samples live in a tilde folder Unity ignores, so copy them in to compile them.
$samplesSource = Join-Path $packageRoot 'Samples~'
$samplesTarget = Join-Path $ProjectPath 'Assets\Samples'

if (Test-Path $samplesTarget) {
    Remove-Item $samplesTarget -Recurse -Force
}

if (Test-Path $samplesSource) {
    New-Item -ItemType Directory -Force -Path $samplesTarget | Out-Null
    Copy-Item (Join-Path $samplesSource '*') $samplesTarget -Recurse -Force
    Write-Host "Samples : copied for compilation"
}
else {
    Write-Host "Samples : none found"
}

# -- Run the tests ------------------------------------------------------------

$resultsPath = Join-Path $ProjectPath 'results.xml'
$logPath = Join-Path $ProjectPath 'unity.log'

if (Test-Path $resultsPath) {
    Remove-Item $resultsPath -Force
}

Write-Host "Project : $ProjectPath"
Write-Host ''
Write-Host 'Running Play Mode tests. First run reimports the project and takes a few minutes.'

$arguments = @(
    '-batchmode'
    '-nographics'
    '-projectPath'; $ProjectPath
    '-runTests'
    '-testPlatform'; 'PlayMode'
    '-testResults'; $resultsPath
    '-logFile'; $logPath
)

$process = Start-Process -FilePath $unityExe -ArgumentList $arguments -Wait -PassThru -NoNewWindow
$unityExit = $process.ExitCode

# -- Report -------------------------------------------------------------------

Write-Host ''

if (-not (Test-Path $resultsPath)) {
    Write-Host 'No test results were produced.' -ForegroundColor Red

    if (Test-Path $logPath) {
        $compileErrors = @(Select-String -Path $logPath -Pattern 'error CS\d+' | Select-Object -First 15)

        if ($compileErrors.Count -gt 0) {
            Write-Host ''
            Write-Host 'Compiler errors:' -ForegroundColor Red
            foreach ($line in $compileErrors) {
                Write-Host "  $($line.Line.Trim())"
            }
        }
        else {
            # Unity fails before compiling for plenty of reasons that are not compiler
            # errors -- a malformed manifest, an unresolvable package, a licence
            # problem -- and none of them mention "error CS". Show the end of the log
            # so the cause is on screen rather than a file path away.
            Write-Host ''
            Write-Host 'No compiler errors found. Last lines of the Unity log:' -ForegroundColor Yellow
            foreach ($line in (Get-Content $logPath -Tail 20)) {
                Write-Host "  $line"
            }
        }

        Write-Host ''
        Write-Host "Full log: $logPath"
    }

    exit 1
}

[xml] $results = Get-Content $resultsPath -Raw
$run = $results.'test-run'

$total = [int] $run.total
$passed = [int] $run.passed
$failed = [int] $run.failed
$skipped = [int] $run.skipped

$failures = @($results.SelectNodes("//test-case[@result='Failed']"))

if ($failures.Count -gt 0) {
    Write-Host 'Failed tests:' -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  $($failure.fullname)" -ForegroundColor Red
        $message = $failure.failure.message
        if (-not [string]::IsNullOrWhiteSpace($message)) {
            Write-Host "    $(($message -split "`n")[0].Trim())"
        }
    }
    Write-Host ''
}

$summary = "$passed passed, $failed failed, $skipped skipped, $total total"

if ($failed -eq 0 -and $unityExit -eq 0) {
    Write-Host $summary -ForegroundColor Green
    Write-Host "Log: $logPath"
    exit 0
}

Write-Host $summary -ForegroundColor Red
Write-Host "Unity exit code: $unityExit"
Write-Host "Log: $logPath"
exit 1
