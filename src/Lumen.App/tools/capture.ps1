<#
.SYNOPSIS
    Unattended screenshot / evidence harness for the Lumen v2 (batch) WinUI 3 app.

.DESCRIPTION
    Drives the app through debug-only environment variables (never shown in UI):
      LUMEN_OPEN / LUMEN_OPEN_MANY   add one / many (';' separated) images on startup
      LUMEN_SCALE=2|3|4             preset target scale
      LUMEN_BACKEND=auto|gpu|cpu    override processing mode
      LUMEN_CONFLICT=rename|overwrite|skip   override conflict policy
      LUMEN_PATTERN=<template>      override output file name template
      LUMEN_SAVE_DIR=<dir>          batch output directory
      LUMEN_AUTOSTART=1             start batch processing automatically
      LUMEN_FORCE_NO_DML=1          make UI treat DirectML as unavailable (compat path)
      LUMEN_THEME=Dark|Light        force root element theme

    Captures exactly the window of the process it launched (by PID) so other Lumen
    instances never confuse the capture. Never kills processes by name.

    This file is intentionally ASCII-only: it is run by Windows PowerShell 5.1,
    which reads BOM-less .ps1 files as ANSI.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File src\Lumen.App\tools\capture.ps1 -Scenario batch-gpu-dark
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("empty-dark", "waiting-dark", "batch-gpu-dark", "batch-gpu-9", "cpu-dark",
                 "system-info-dark", "no-dml-dark", "light-set")]
    [string]$Scenario
)

$ErrorActionPreference = "Stop"

$workspace = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$outDir = Join-Path $workspace "docs\screenshots"
$testdata = Join-Path $PSScriptRoot "testdata"
$animeDir = Join-Path $workspace "assets\samples\anime"
$batchRoot = Join-Path $workspace "artifacts\ui-batch"
New-Item -ItemType Directory -Force -Path $outDir, $batchRoot | Out-Null

if (-not ("LumenCapture.Win32" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace LumenCapture {
    public static class Win32 {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        public const uint PW_RENDERFULLCONTENT = 2;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    }
}
"@
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

function Get-LumenExe {
    $exe = Get-ChildItem (Join-Path $workspace "src\Lumen.App\bin") -Recurse -Filter Lumen.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $exe) {
        throw "Lumen.exe not found. Build src/Lumen.App first."
    }
    return $exe.FullName
}

function Start-Lumen {
    param([hashtable]$Environment)

    $exe = Get-LumenExe
    $previous = @{}
    foreach ($key in $Environment.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $Environment[$key])
    }

    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru

    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key])
    }

    Start-Sleep -Seconds 2
    return $process
}

function Stop-LumenProcess {
    param([int]$ProcessId)
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Get-WindowHandle {
    param([int]$ProcessId)

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($process -and $process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $process.MainWindowHandle
        }
        Start-Sleep -Milliseconds 300
    }

    throw "No visible main window for process $ProcessId."
}

function Capture-Window {
    param([int]$ProcessId, [string]$FileName, [switch]$ScreenFallback)

    $hwnd = Get-WindowHandle -ProcessId $ProcessId
    [void][LumenCapture.Win32]::ShowWindow($hwnd, 9)
    [void][LumenCapture.Win32]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 700

    $rect = New-Object LumenCapture.Win32+RECT
    $dwmResult = [LumenCapture.Win32]::DwmGetWindowAttribute(
        $hwnd,
        [LumenCapture.Win32]::DWMWA_EXTENDED_FRAME_BOUNDS,
        [ref]$rect,
        [System.Runtime.InteropServices.Marshal]::SizeOf([type][LumenCapture.Win32+RECT]))
    if ($dwmResult -ne 0) {
        [void][LumenCapture.Win32]::GetWindowRect($hwnd, [ref]$rect)
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "Invalid window size: ${width}x${height}"
    }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    if ($ScreenFallback) {
        # Dialog / popup layers may live outside PrintWindow's surface; grab from screen.
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
    }
    else {
        $hdc = $graphics.GetHdc()
        try {
            $ok = [LumenCapture.Win32]::PrintWindow($hwnd, $hdc, [LumenCapture.Win32]::PW_RENDERFULLCONTENT)
        } finally {
            $graphics.ReleaseHdc($hdc)
        }
        if (-not $ok) {
            throw "PrintWindow failed."
        }
    }

    $graphics.Dispose()
    $outputPath = Join-Path $outDir $FileName
    $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "Screenshot saved: $outputPath (${width}x${height})"
}

function Find-UiaElementOnce {
    param([int]$ProcessId, [string]$AutomationId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $idCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $idCondition)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-UiaElement {
    param([int]$ProcessId, [string]$AutomationId, [int]$TimeoutSeconds = 20)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $element = Find-UiaElementOnce -ProcessId $ProcessId -AutomationId $AutomationId
        if ($element) {
            return $element
        }
        Start-Sleep -Milliseconds 400
    }

    return $null
}

function Get-UiaName {
    param([int]$ProcessId, [string]$AutomationId)

    $element = Find-UiaElement -ProcessId $ProcessId -AutomationId $AutomationId -TimeoutSeconds 8
    if ($element) {
        return $element.Current.Name
    }
    return ""
}

function Invoke-UiaButton {
    param([int]$ProcessId, [string]$AutomationId)

    $element = Find-UiaElement -ProcessId $ProcessId -AutomationId $AutomationId -TimeoutSeconds 15
    if (-not $element) {
        throw "UIA button not found: $AutomationId"
    }

    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Write-Host "UIA invoked: $AutomationId"
}

function Get-QueueItemNames {
    param([int]$ProcessId, [string]$QueueAutomationId = "QueueList")

    $list = Find-UiaElement -ProcessId $ProcessId -AutomationId $QueueAutomationId -TimeoutSeconds 10
    if (-not $list) {
        return @()
    }

    $childCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $childCondition)
    $names = @()
    foreach ($item in $items) {
        $names += $item.Current.Name
    }
    return $names
}

function Test-ProcessingUiVisible {
    param([int]$ProcessId)

    $cancel = Find-UiaElementOnce -ProcessId $ProcessId -AutomationId "BtnCancel"
    if ($cancel) { return $true }
    $progress = Find-UiaElementOnce -ProcessId $ProcessId -AutomationId "TxtBatchProgress"
    if ($progress) { return $true }
    return $false
}

function Wait-LumenBatchDone {
    param([int]$ProcessId, [int]$TimeoutSeconds = 300)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    # 1) Wait until the processing UI appears (or give up after 20 s and read current state).
    $started = $false
    $startDeadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $startDeadline) {
        if (Test-ProcessingUiVisible -ProcessId $ProcessId) {
            $started = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }

    if ($started) {
        # 2) Wait until both the cancel button and the batch progress text are gone.
        $absentChecks = 0
        while ((Get-Date) -lt $deadline -and $absentChecks -lt 2) {
            if (Test-ProcessingUiVisible -ProcessId $ProcessId) {
                $absentChecks = 0
            }
            else {
                $absentChecks++
            }
            Start-Sleep -Milliseconds 700
        }
    }
    else {
        Start-Sleep -Seconds 8
    }

    Start-Sleep -Milliseconds 500
    return @{
        Status = (Get-UiaName -ProcessId $ProcessId -AutomationId "TxtStatus")
        Detail = (Get-UiaName -ProcessId $ProcessId -AutomationId "TxtStatusDetail")
    }
}

function Write-QueueEvidence {
    param([int]$ProcessId)

    Write-Host "Queue summary: $(Get-UiaName -ProcessId $ProcessId -AutomationId 'TxtQueueSummary')"
    Write-Host "Backend      : $(Get-UiaName -ProcessId $ProcessId -AutomationId 'TxtBackend')"
    Write-Host "Status       : $(Get-UiaName -ProcessId $ProcessId -AutomationId 'TxtStatus')"
    Write-Host "Status detail: $(Get-UiaName -ProcessId $ProcessId -AutomationId 'TxtStatusDetail')"
    $names = Get-QueueItemNames -ProcessId $ProcessId
    Write-Host "Queue items ($($names.Count)):"
    foreach ($name in $names) {
        Write-Host "  - $name"
    }
}

function Write-OutputEvidence {
    param([string]$Directory)

    if (-not (Test-Path $Directory)) {
        Write-Host "OUTPUT DIR MISSING: $Directory"
        return
    }

    Write-Host "Output files in ${Directory}:"
    Get-ChildItem $Directory -File | ForEach-Object {
        try {
            $image = [System.Drawing.Image]::FromFile($_.FullName)
            Write-Host ("  - {0}  {1} B  {2}x{3}" -f $_.Name, $_.Length, $image.Width, $image.Height)
            $image.Dispose()
        } catch {
            Write-Host ("  - {0}  {1} B  (not an image)" -f $_.Name, $_.Length)
        }
    }
}

function New-TempOutputDirectory {
    param([string]$Name)

    $directory = Join-Path $batchRoot $Name
    if (Test-Path $directory) {
        Remove-Item $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    return $directory
}

$anime1 = Join-Path $animeDir "Anime_girl_with_red_eyes.png"
$anime2 = Join-Path $animeDir "Red_suit_anime_girl.png"
$anime3 = Join-Path $animeDir "Moe_anime_girl_in_sailor_fuku.png"
$corrupt = Join-Path $testdata "anime-corrupt.png"
$smallAnime = Join-Path $testdata "anime-small.png"
$manyPaths = @($anime1, $anime2, $anime3, $corrupt) -join ';'

Write-Host "Scenario: $Scenario"
Write-Host "EXE: $(Get-LumenExe)"

$app = $null

switch ($Scenario) {
    "empty-dark" {
        $app = Start-Lumen @{}
        Capture-Window -ProcessId $app.Id -FileName "20-empty-queue-dark.png"
    }
    "waiting-dark" {
        $app = Start-Lumen @{ LUMEN_OPEN_MANY = $manyPaths }
        Start-Sleep -Seconds 5
        Capture-Window -ProcessId $app.Id -FileName "21-queue-waiting-dark.png"
        Write-QueueEvidence -ProcessId $app.Id
    }
    "batch-gpu-dark" {
        $out = New-TempOutputDirectory -Name "gpu"
        $app = Start-Lumen @{
            LUMEN_OPEN_MANY = $manyPaths
            LUMEN_SAVE_DIR = $out
            LUMEN_AUTOSTART = "1"
            LUMEN_CONFLICT = "rename"
        }
        Start-Sleep -Seconds 2
        Capture-Window -ProcessId $app.Id -FileName "22-batch-processing-dark.png"
        Write-Host "Processing detail: $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtStatusDetail')"
        $done = Wait-LumenBatchDone -ProcessId $app.Id -TimeoutSeconds 300
        Start-Sleep -Seconds 1
        Capture-Window -ProcessId $app.Id -FileName "23-batch-done-dark.png"
        Write-QueueEvidence -ProcessId $app.Id
        Write-Host "Batch result: $($done.Status) | $($done.Detail)"
        Write-OutputEvidence -Directory $out

        # Conflict=skip: a pre-existing sentinel must not be touched (Skipped item).
        $skipDir = New-TempOutputDirectory -Name "skip"
        $sentinel = Join-Path $skipDir "Anime_girl_with_red_eyes_4x.png"
        [System.IO.File]::WriteAllText($sentinel, "sentinel-content-must-not-change")
        $before = (Get-FileHash $sentinel -Algorithm SHA256).Hash
        $skipApp = Start-Lumen @{
            LUMEN_OPEN_MANY = $anime1
            LUMEN_SAVE_DIR = $skipDir
            LUMEN_CONFLICT = "skip"
            LUMEN_AUTOSTART = "1"
        }
        $skipDone = Wait-LumenBatchDone -ProcessId $skipApp.Id -TimeoutSeconds 180
        $after = (Get-FileHash $sentinel -Algorithm SHA256).Hash
        Write-Host "SKIP sentinel unchanged: $($before -eq $after)"
        Write-Host "SKIP status: $($skipDone.Status) | $($skipDone.Detail)"
        Write-Host "SKIP queue item names: $((Get-QueueItemNames -ProcessId $skipApp.Id) -join ' | ')"
        Write-OutputEvidence -Directory $skipDir
        Stop-LumenProcess -ProcessId $skipApp.Id
    }
    "batch-gpu-9" {
        # QA P1 regression: 9 images (> MaxCachedResultPreviews=8) must finish without
        # "Collection was modified" and report success 9 / failure 0.
        $nine = @(1..9 | ForEach-Object { Join-Path $testdata ("gpu9\anime-{0:00}.png" -f $_) })
        $out = New-TempOutputDirectory -Name "gpu9"
        $app = Start-Lumen @{
            LUMEN_OPEN_MANY = ($nine -join ';')
            LUMEN_SAVE_DIR = $out
            LUMEN_AUTOSTART = "1"
            LUMEN_CONFLICT = "rename"
        }
        Start-Sleep -Seconds 3
        Capture-Window -ProcessId $app.Id -FileName "30-batch-9-processing-dark.png"
        $done = Wait-LumenBatchDone -ProcessId $app.Id -TimeoutSeconds 360
        Start-Sleep -Seconds 1
        Capture-Window -ProcessId $app.Id -FileName "30-batch-9-done-dark.png"
        Write-QueueEvidence -ProcessId $app.Id
        Write-Host "Batch-9 result: $($done.Status) | $($done.Detail)"
        $infoBarFolderButton = Find-UiaElement -ProcessId $app.Id -AutomationId "InfoBarStatus" -TimeoutSeconds 5
        if ($infoBarFolderButton) {
            Write-Host "InfoBar present: name='$($infoBarFolderButton.Current.Name)'"
        }
        Write-OutputEvidence -Directory $out
    }    "cpu-dark" {
        $out = New-TempOutputDirectory -Name "cpu"
        $app = Start-Lumen @{
            LUMEN_OPEN_MANY = $smallAnime
            LUMEN_SAVE_DIR = $out
            LUMEN_BACKEND = "cpu"
            LUMEN_SCALE = "2"
            LUMEN_AUTOSTART = "1"
        }
        Start-Sleep -Seconds 2
        Capture-Window -ProcessId $app.Id -FileName "24-cpu-mode-processing-dark.png"
        Write-Host "CPU processing detail: $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtStatusDetail')"
        $done = Wait-LumenBatchDone -ProcessId $app.Id -TimeoutSeconds 420
        Start-Sleep -Seconds 1
        Capture-Window -ProcessId $app.Id -FileName "24-cpu-mode-dark.png"
        Write-QueueEvidence -ProcessId $app.Id
        Write-Host "CPU result: $($done.Status) | $($done.Detail)"
        Write-OutputEvidence -Directory $out
    }
    "system-info-dark" {
        $app = Start-Lumen @{ LUMEN_OPEN_MANY = $manyPaths }
        Start-Sleep -Seconds 5
        Invoke-UiaButton -ProcessId $app.Id -AutomationId "BtnSystemInfo"
        Start-Sleep -Seconds 2
        Capture-Window -ProcessId $app.Id -FileName "25-system-info-dark.png" -ScreenFallback
        Write-Host "Sys OS    : $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtSysOs')"
        Write-Host "Sys CPU   : $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtSysCpu')"
        Write-Host "Sys GPU   : $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtSysGpu')"
        Write-Host "Sys DML   : $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtSysDirectMl')"
        Write-Host "Sys models: $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtSysModels')"
        Write-Host "Sys log   : $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtSysLogPath')"
    }
    "no-dml-dark" {
        $app = Start-Lumen @{ LUMEN_OPEN_MANY = $manyPaths; LUMEN_FORCE_NO_DML = "1" }
        Start-Sleep -Seconds 5
        Capture-Window -ProcessId $app.Id -FileName "26-force-no-dml-dark.png"
        Write-Host "No-DML mode hint: $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtComputeModeHint')"
        Write-Host "No-DML backend  : $(Get-UiaName -ProcessId $app.Id -AutomationId 'TxtBackend')"
    }
    "light-set" {
        $app = Start-Lumen @{ LUMEN_THEME = "Light" }
        Capture-Window -ProcessId $app.Id -FileName "27-empty-queue-light.png"
        Stop-LumenProcess -ProcessId $app.Id

        $app = Start-Lumen @{ LUMEN_THEME = "Light"; LUMEN_OPEN_MANY = $manyPaths }
        Start-Sleep -Seconds 5
        Capture-Window -ProcessId $app.Id -FileName "28-queue-waiting-light.png"
        Stop-LumenProcess -ProcessId $app.Id

        # Light batch completion uses CPU compatibility mode (no GPU needed, keeps QA windows free).
        $out = New-TempOutputDirectory -Name "cpu-light"
        $app = Start-Lumen @{
            LUMEN_THEME = "Light"
            LUMEN_OPEN_MANY = $smallAnime
            LUMEN_SAVE_DIR = $out
            LUMEN_BACKEND = "cpu"
            LUMEN_SCALE = "2"
            LUMEN_AUTOSTART = "1"
        }
        $done = Wait-LumenBatchDone -ProcessId $app.Id -TimeoutSeconds 300
        Start-Sleep -Seconds 1
        Capture-Window -ProcessId $app.Id -FileName "29-batch-done-light.png"
        Write-QueueEvidence -ProcessId $app.Id
        Write-Host "Light batch result: $($done.Status) | $($done.Detail)"
        Write-OutputEvidence -Directory $out
    }
}

if ($app) {
    Stop-LumenProcess -ProcessId $app.Id
}

Write-Host "Done: $Scenario"
