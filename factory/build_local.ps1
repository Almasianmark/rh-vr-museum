<#
.SYNOPSIS
  Build museum ports on this Windows PC with Unity Hub editors, then smoke-test each on a USB-connected Quest 2.

.DESCRIPTION
  For each project id: prepare (clone + LFS + kit injection) -> install the project's exact Unity editor with
  Android support if missing -> batch-mode build through the kit's PortRecipe -> install, launch and 60 s
  logcat check on the headset -> triage into data/port_status.json and factory/TRIAGE.md.
  Uses the Unity licence already activated in Unity Hub (Personal is fine). No Docker needed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File factory\build_local.ps1
  powershell -ExecutionPolicy Bypass -File factory\build_local.ps1 -Ids 2026-battlefish -SkipSmoke
#>
param(
    # First ports: Quest 2 natives with no LFS problems (see factory/README.md "First ports").
    [string[]]$Ids = @("2026-battlefish", "2025-caregivr-tzmqkg", "2026-memory-tree-i2lsr9"),
    [string]$HubEditors = "C:\Program Files\Unity\Hub\Editor",
    [string]$HubExe = "C:\Program Files\Unity Hub\Unity Hub.exe",
    [switch]$SkipSmoke,         # build only
    [switch]$NoInstall,         # never install editors; fail the project instead
    [switch]$AllowNewerPatch,   # use an installed editor of the same major.minor with a higher patch
    [int]$SmokeSeconds = 60
)
$ErrorActionPreference = "Stop"
$Ids = @($Ids | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })   # -File passes "a,b" as one string
$Root = Split-Path -Parent $PSScriptRoot
$Pipeline = Join-Path $Root "pipeline"
$Work = Join-Path $Root "factory\work"
$Queue = (Get-Content (Join-Path $Root "data\port_queue.json") -Raw -Encoding UTF8 | ConvertFrom-Json).entries

function Find-Python {
    foreach ($c in @("py", "python", "python3")) {
        if (Get-Command $c -ErrorAction SilentlyContinue) { return $c }
    }
    throw "Python 3.9+ not found. Install it from python.org (tick 'Add to PATH')."
}

function Invoke-Py([string[]]$PyArgs) {
    Push-Location $Pipeline
    try {
        $env:PYTHONUTF8 = "1"
        & $script:Py -m @PyArgs
        if ($LASTEXITCODE -ne 0) { throw "python -m $($PyArgs -join ' ') failed ($LASTEXITCODE)" }
    } finally { Pop-Location }
}

function Get-VersionKey([string]$v) {
    # 2022.3.19f1 -> [2022, 3, 19, 1]
    $m = [regex]::Match($v, '^(\d+)\.(\d+)\.(\d+)[a-z](\d+)')
    if (-not $m.Success) { return $null }
    return @([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value, [int]$m.Groups[4].Value)
}

function Find-Editor([string]$Version) {
    $exact = Join-Path $HubEditors "$Version\Editor\Unity.exe"
    if (Test-Path $exact) { return $exact }
    if (-not $AllowNewerPatch) { return $null }
    $want = Get-VersionKey $Version
    $best = $null; $bestKey = $null
    foreach ($d in (Get-ChildItem $HubEditors -Directory -ErrorAction SilentlyContinue)) {
        $k = Get-VersionKey $d.Name
        if ($null -eq $k -or $k[0] -ne $want[0] -or $k[1] -ne $want[1]) { continue }
        if ($k[2] -lt $want[2]) { continue }
        $exe = Join-Path $d.FullName "Editor\Unity.exe"
        if ((Test-Path $exe) -and ($null -eq $bestKey -or $k[2] -gt $bestKey[2])) { $best = $exe; $bestKey = $k }
    }
    return $best
}

function Install-Editor([string]$Version, [string]$Changeset) {
    if (-not (Test-Path $HubExe)) { throw "Unity Hub not found at $HubExe" }
    Write-Host "  installing Unity $Version + Android (SDK/NDK/JDK) through Unity Hub; this takes 10-30 min..."
    $hubArgs = "-- --headless install --version $Version --module android --childModules"
    if ($Changeset) { $hubArgs += " --changeset $Changeset" }
    # Start-Process keeps the leading "--" (PowerShell can swallow it when calling an exe directly).
    Start-Process -FilePath $HubExe -ArgumentList $hubArgs -Wait -NoNewWindow
}

function Find-Adb([string]$Editor) {
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($ed in @($Editor) + (Get-ChildItem $HubEditors -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName "Editor\Unity.exe" })) {
        if (-not $ed) { continue }
        $adb = Join-Path (Split-Path $ed) "Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
        if (Test-Path $adb) { return $adb }
    }
    return $null
}

function Quote([string]$s) { return '"' + $s.Replace('"', "'") + '"' }

# ------------------------------------------------------------------ checks

$Py = Find-Python
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "git not found. Install Git for Windows (it includes Git LFS)." }
& git lfs version | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Git LFS missing: run 'git lfs install' (Git for Windows ships it)." }
Push-Location $Pipeline; & $Py -m pip install -q -r requirements.txt; Pop-Location

$results = @()
foreach ($id in $Ids) {
    $e = $Queue | Where-Object { $_.id -eq $id }
    if (-not $e) { Write-Warning "$id is not in data/port_queue.json; skipped"; continue }
    Write-Host ""
    Write-Host "=== $($e.rank). $($e.title)  ($id, Unity $($e.unity_version), recipe $($e.recipe))" -ForegroundColor Cyan
    $out = Join-Path $Work "_out\$id"
    $apk = Join-Path $out "app.apk"
    $log = Join-Path $out "unity.log"
    $row = [ordered]@{ id = $id; build = "-"; smoke = "-"; fps = ""; note = "" }
    try {
        # 1. Prepare: clone, Git LFS, kit injection, package upgrades (idempotent).
        Invoke-Py @("rhm.factory", "prepare", $id, "--work", $Work) | Out-Null
        $project = Join-Path $Work (Join-Path $id $e.project_path)

        # 2. Editor
        $unity = Find-Editor $e.unity_version
        if (-not $unity -and -not $NoInstall) { Install-Editor $e.unity_version $e.changeset; $unity = Find-Editor $e.unity_version }
        if (-not $unity) { throw "Unity $($e.unity_version) is not installed (rerun without -NoInstall, or with -AllowNewerPatch)" }
        Write-Host "  editor: $unity"

        # 3. Build. The first import of a project can take 20-60 min; progress goes to the log.
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        if (Test-Path $apk) { Remove-Item $apk }
        Write-Host "  building... (log: $log)"
        $unityArgs = @("-batchmode", "-quit", "-logFile", (Quote $log), "-projectPath", (Quote $project),
            "-buildTarget", "Android", "-executeMethod", "RealityHack.MuseumKit.Editor.PortRecipe.Run",
            "-rhOutput", (Quote $apk), "-rhPackageId", $e.package_id, "-rhProductName", (Quote $e.title),
            "-rhRecipe", $e.recipe) -join " "
        $p = Start-Process -FilePath $unity -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
        if ($p.ExitCode -ne 0 -or -not (Test-Path $apk)) {
            $row.build = "FAIL ($($p.ExitCode))"
            $row.note = "see $log; last errors below"
            Write-Host "  build failed. Last errors:" -ForegroundColor Red
            if (Test-Path $log) { Select-String -Path $log -Pattern "error CS|Error|Exception" | Select-Object -Last 12 | ForEach-Object { "    " + $_.Line } }
            $results += [pscustomobject]$row
            continue
        }
        $row.build = "ok ({0:N0} MB)" -f ((Get-Item $apk).Length / 1MB)

        # 4. Smoke test on the headset
        if (-not $SkipSmoke) {
            $adb = Find-Adb $unity
            if (-not $adb) { throw "adb not found" }
            $devices = & $adb devices | Select-String "\tdevice$"
            if (-not $devices) { $row.smoke = "skipped"; $row.note = "no headset on adb"; Write-Warning "no Quest connected; smoke skipped" }
            else {
                Write-Host "  smoke test: install, launch, $SmokeSeconds s logcat (put the headset on to keep it awake)"
                Invoke-Py @("rhm.factory", "smoke", $id, "--apk", $apk, "--seconds", "$SmokeSeconds", "--adb", $adb) | Out-Null
                $sm = Get-Content (Join-Path $Root "factory\reports\$id\smoke.json") -Raw -Encoding UTF8 | ConvertFrom-Json
                $row.smoke = $sm.verdict
                if ($sm.fps_median) { $row.fps = $sm.fps_median }
            }
        }
    } catch {
        $row.note = $_.Exception.Message
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    }
    $results += [pscustomobject]$row
}

# 5. Triage: port_status.json + TRIAGE.md (only ports that passed the smoke test become installable in the museum)
Invoke-Py @("rhm.factory", "triage", "--work", $Work) | Out-Null
Write-Host ""
Write-Host ($results | Format-Table -AutoSize | Out-String -Width 220)
Write-Host "Reports: factory\reports\<id>\ (smoke.json, logcat.txt, screenshot.png); summary: factory\TRIAGE.md"
Write-Host "Send back: factory\TRIAGE.md, factory\reports\*\smoke.json, and unity.log of any failed build."
