#requires -Version 5.1
<#
.SYNOPSIS
    Build, launch, wait for, screenshot and close the Lumen WinUI 3 smoke app.

.DESCRIPTION
    One-shot verification recipe for WinUI 3 on a machine with:
      * .NET SDK installed in a custom directory (not on PATH)
      * no Visual Studio and no Windows SDK installed
      * no Developer Mode and no administrator rights
      * unpackaged + WindowsAppSDKSelfContained=true x64 app

    It builds the project with `dotnet build`, starts the exe directly (no MSIX
    deployment / no winapp run), waits for the top-level window, captures it two
    ways (CopyFromScreen and PrintWindow/PW_RENDERFULLCONTENT), validates that
    the result is real window content (not black / not a solid color), asserts
    the Mica + custom-titlebar status reported by the app, then closes the app.

    Every file path is absolute: .NET APIs such as Bitmap.Save resolve relative
    paths against the process working directory, not PowerShell's location.

.PARAMETER Configuration
    Build configuration. Default: Debug.

.PARAMETER Platform
    MSBuild platform. Must stay x64 (AnyCPU is not supported by WinUI 3). Default: x64.

.PARAMETER NoBuild
    Skip `dotnet build` and reuse the existing output.

.PARAMETER KeepAlive
    Leave the app running after the screenshot (prints the PID).

.PARAMETER ScreenshotPath
    Primary screenshot output. Default: <repo>/spike/winui-smoke/screenshot.png

.PARAMETER DotnetRoot
    Directory that contains dotnet.exe when .NET is not on PATH.
    Default: C:\Users\0x7c14\dotnet10

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\winui-spike.ps1

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\winui-spike.ps1 -NoBuild -KeepAlive
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64')][string]$Platform = 'x64',
    [switch]$NoBuild,
    [switch]$KeepAlive,
    [string]$ScreenshotPath,
    [string]$DotnetRoot = 'C:\Users\0x7c14\dotnet10',
    [int]$LaunchTimeoutSeconds = 40
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------------------
# Paths (all absolute; .NET file APIs do not use PowerShell's location)
# ---------------------------------------------------------------------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectDir = Join-Path $repoRoot 'spike\winui-smoke'
$projectFile = Join-Path $projectDir 'WinUISmoke.csproj'
$tfms = 'net10.0-windows10.0.26100.0'
$outputDir = Join-Path $projectDir ("bin\{0}\{1}\{2}\win-x64" -f $Platform, $Configuration, $tfms)
$exePath = Join-Path $outputDir 'WinUISmoke.exe'
$statusFile = Join-Path $outputDir 'smoke-status.txt'
if (-not $ScreenshotPath) { $ScreenshotPath = Join-Path $projectDir 'screenshot.png' }
$ScreenshotPath = [IO.Path]::GetFullPath($ScreenshotPath)
$printWindowPath = [IO.Path]::GetFullPath(($ScreenshotPath -replace '\.png$', '') + '-printwindow.png')
$startedAt = Get-Date

function Write-Step([string]$message) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $message)
}

# ---------------------------------------------------------------------------
# 0. Resolve dotnet.exe
# ---------------------------------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $candidate = Join-Path $DotnetRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "dotnet not found on PATH and '$candidate' does not exist. Pass -DotnetRoot <dir>."
    }
    $env:Path = (Split-Path -Parent $candidate) + ';' + $env:Path
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
}
Write-Step ("dotnet: {0} ({1})" -f (& $dotnet.Source --version), $dotnet.Source)

if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "Project not found: $projectFile"
}

# ---------------------------------------------------------------------------
# 1. Kill leftovers (a running exe locks bin\...\WinUISmoke.exe and breaks the build)
# ---------------------------------------------------------------------------
Get-Process -Name 'WinUISmoke' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Step "closing leftover WinUISmoke process $($_.Id)"
    try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { }
}
Start-Sleep -Milliseconds 300

# ---------------------------------------------------------------------------
# 2. Build (JIT, no MSIX, no Developer Mode)
# ---------------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Step "building $projectFile (-c $Configuration -p:Platform=$Platform)"
    $buildLog = & $dotnet.Source build $projectFile -c $Configuration -p:Platform=$Platform -v minimal 2>&1
    $buildExit = $LASTEXITCODE
    $buildLog | ForEach-Object { Write-Host "  $_" }
    if ($buildExit -ne 0) {
        $tail = ($buildLog | Select-Object -Last 40) -join [Environment]::NewLine
        throw "dotnet build failed with exit code $buildExit. Last lines:`n$tail"
    }
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Build output missing: $exePath (run without -NoBuild first)"
}

# ---------------------------------------------------------------------------
# 3. Win32 helpers (P/Invoke; no UIA / no winapp dependency)
# ---------------------------------------------------------------------------
if (-not ('LumenWinUtil' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class LumenWinUtil
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }

    public static List<IntPtr> VisibleWindowsForProcess(uint pid)
    {
        List<IntPtr> list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Title(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // DWMWA_EXTENDED_FRAME_BOUNDS (9): visible window frame without the invisible
    // resize border that GetWindowRect includes.
    public static RECT FrameBounds(IntPtr h)
    {
        RECT r;
        int hr = DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT)));
        if (hr != 0)
        {
            GetWindowRect(h, out r);
        }
        return r;
    }

    public static void BringToFront(IntPtr h)
    {
        ShowWindow(h, 9); // SW_RESTORE
        SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_TOPMOST, NOSIZE|NOMOVE|SHOWWINDOW
        SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_NOTOPMOST
        SetForegroundWindow(h);
    }
}
"@ | Out-Null
}

# ---------------------------------------------------------------------------
# 4. Launch the exe directly (unpackaged app: this is the reliable path)
# ---------------------------------------------------------------------------
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exePath
$psi.WorkingDirectory = $outputDir   # assets / .pri are resolved from the app directory
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)
Write-Step ("launched {0} (PID {1})" -f $exePath, $proc.Id)

$hwnd = [IntPtr]::Zero
$windowTitle = ''
$className = ''
$result = $null
try {
    $deadline = (Get-Date).AddSeconds($LaunchTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        if ($proc.HasExited) {
            throw "app exited during startup with code $($proc.ExitCode) (crash before first window?)"
        }
        foreach ($candidate in [LumenWinUtil]::VisibleWindowsForProcess([uint32]$proc.Id)) {
            if ([LumenWinUtil]::Title($candidate) -ne '') {
                $hwnd = $candidate
                $windowTitle = [LumenWinUtil]::Title($candidate)
                $className = [LumenWinUtil]::ClassName($candidate)
                break
            }
        }
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        throw "no visible top-level window appeared within ${LaunchTimeoutSeconds}s"
    }
    Write-Step ("window found: hwnd=0x{0:X} class='{1}' title='{2}'" -f $hwnd.ToInt64(), $className, $windowTitle)

    # Wait for first render, then bring the window to the foreground so
    # CopyFromScreen captures the app and not whatever overlaps it.
    Start-Sleep -Seconds 3
    [LumenWinUtil]::BringToFront($hwnd)
    Start-Sleep -Milliseconds 800

    # ------------------------------------------------------------------
    # 5. Structural checks: custom title bar + status file from the app
    # ------------------------------------------------------------------
    $winRect = New-Object LumenWinUtil+RECT
    [void][LumenWinUtil]::GetWindowRect($hwnd, [ref]$winRect)
    $clientRect = New-Object LumenWinUtil+RECT
    [void][LumenWinUtil]::GetClientRect($hwnd, [ref]$clientRect)
    $clientOrigin = New-Object LumenWinUtil+POINT
    [void][LumenWinUtil]::ClientToScreen($hwnd, [ref]$clientOrigin)
    $captionBand = $clientOrigin.Y - $winRect.Top
    $customTitleBar = $captionBand -le 2
    Write-Step ("custom title bar extended: {0} (client top offset {1}px; no native caption band when true)" -f $customTitleBar, $captionBand)

    $status = @{}
    if (Test-Path -LiteralPath $statusFile) {
        foreach ($line in (Get-Content -LiteralPath $statusFile)) {
            $key, $value = $line -split '=', 2
            if ($key) { $status[$key.Trim()] = $value }
        }
    }
    foreach ($required in @('micaSupported', 'acrylicSupported', 'backdrop', 'extendsContentIntoTitleBar', 'windowPx')) {
        if (-not $status.ContainsKey($required)) {
            throw "status file '$statusFile' is missing key '$required' (app did not report its backdrop state)"
        }
    }
    if ($status['micaSupported'] -ne 'True') {
        throw "MicaController.IsSupported() returned $($status['micaSupported']) - Mica is not available on this machine"
    }
    if ($status['backdrop'] -ne 'Mica') {
        throw "app fell back to backdrop '$($status['backdrop'])' instead of Mica"
    }
    if ($status['extendsContentIntoTitleBar'] -ne 'True') {
        throw "ExtendsContentIntoTitleBar=false; custom title bar was not applied"
    }
    Write-Step ("app status: backdrop={0} micaSupported={1} acrylicSupported={2} windowPx={3}" -f `
        $status['backdrop'], $status['micaSupported'], $status['acrylicSupported'], $status['windowPx'])

    # ------------------------------------------------------------------
    # 6. Capture: CopyFromScreen (primary) + PrintWindow (fallback/proof)
    # ------------------------------------------------------------------
    $bounds = [LumenWinUtil]::FrameBounds($hwnd)
    $captureWidth = $bounds.Right - $bounds.Left
    $captureHeight = $bounds.Bottom - $bounds.Top
    if ($captureWidth -lt 200 -or $captureHeight -lt 150) {
        throw "unexpected window size ${captureWidth}x${captureHeight}"
    }
    Write-Step ("frame bounds: ({0},{1}) {2}x{3}" -f $bounds.Left, $bounds.Top, $captureWidth, $captureHeight)

    function Test-Capture([string]$path) {
        $bitmap = [System.Drawing.Bitmap]::FromFile($path)
        try {
            $colors = @{}
            $samples = 0
            $stepX = [Math]::Max(1, [int]($bitmap.Width / 48))
            $stepY = [Math]::Max(1, [int]($bitmap.Height / 48))
            for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
                for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
                    $argb = $bitmap.GetPixel($x, $y).ToArgb()
                    if (-not $colors.ContainsKey($argb)) { $colors[$argb] = 0 }
                    $colors[$argb]++
                    $samples++
                }
            }
            return [pscustomobject]@{
                Width        = $bitmap.Width
                Height       = $bitmap.Height
                ColorCount   = $colors.Count
                SampleCount  = $samples
                IsSolidColor = ($colors.Count -le 1)
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    function Save-ScreenCapture([string]$path) {
        $bitmap = New-Object System.Drawing.Bitmap($captureWidth, $captureHeight)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, (New-Object System.Drawing.Size($captureWidth, $captureHeight)))
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }

    function Save-PrintWindowCapture([string]$path) {
        $bitmap = New-Object System.Drawing.Bitmap($captureWidth, $captureHeight)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $hdc = $graphics.GetHdc()
                try {
                    $ok = [LumenWinUtil]::PrintWindow($hwnd, $hdc, 2) # PW_RENDERFULLCONTENT
                }
                finally {
                    $graphics.ReleaseHdc($hdc)
                }
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            return $ok
        }
        finally {
            $bitmap.Dispose()
        }
    }

    foreach ($path in @($ScreenshotPath, $printWindowPath)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        $parent = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    }

    Save-ScreenCapture $ScreenshotPath
    $screenCheck = Test-Capture $ScreenshotPath
    Write-Step ("CopyFromScreen -> {0} ({1}x{2}, {3} distinct colors)" -f $ScreenshotPath, $screenCheck.Width, $screenCheck.Height, $screenCheck.ColorCount)

    $printWindowOk = Save-PrintWindowCapture $printWindowPath
    $printCheck = Test-Capture $printWindowPath
    Write-Step ("PrintWindow(PW_RENDERFULLCONTENT={0}) -> {1} ({2}x{3}, {4} distinct colors)" -f $printWindowOk, $printWindowPath, $printCheck.Width, $printCheck.Height, $printCheck.ColorCount)

    # The primary evidence must look like real UI: many distinct colors, not one flat fill.
    if ($screenCheck.IsSolidColor -or $screenCheck.ColorCount -lt 8) {
        if (-not $printCheck.IsSolidColor -and $printCheck.ColorCount -ge 8) {
            Write-Step "CopyFromScreen looks blank (screen locked / window obscured) - using PrintWindow capture as primary screenshot"
            Copy-Item -LiteralPath $printWindowPath -Destination $ScreenshotPath -Force
            $screenCheck = Test-Capture $ScreenshotPath
        }
        else {
            throw "both captures look blank (screen={0} colors, printwindow={1} colors). The desktop may be locked." -f $screenCheck.ColorCount, $printCheck.ColorCount
        }
    }

    if (-not $customTitleBar) {
        throw "custom title bar check failed: client area starts $captionBand px below the window top"
    }

    $result = [ordered]@{
        ok                   = $true
        hwnd                 = ('0x{0:X}' -f $hwnd.ToInt64())
        windowTitle          = $windowTitle
        windowClass          = $className
        processId            = $proc.Id
        customTitleBar       = $customTitleBar
        captionBandPx        = $captionBand
        micaSupported        = $status['micaSupported']
        acrylicSupported     = $status['acrylicSupported']
        backdrop             = $status['backdrop']
        windowSizePx         = $status['windowPx']
        screenshot           = $ScreenshotPath
        screenshotSize       = ('{0}x{1}' -f $screenCheck.Width, $screenCheck.Height)
        screenshotColors     = $screenCheck.ColorCount
        printWindowScreenshot = $printWindowPath
        printWindowOk        = [bool]$printWindowOk
        elapsedSeconds       = [Math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
    }
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        if ($KeepAlive) {
            Write-Step "KeepAlive: leaving PID $($proc.Id) running (hwnd=0x$($hwnd.ToInt64().ToString('X')))"
            if ($null -ne $result) { $result['keptAlivePid'] = $proc.Id }
        }
        else {
            try {
                $proc.Kill()
                [void]$proc.WaitForExit(5000)
                Write-Step "app closed (PID $($proc.Id))"
            }
            catch { }
        }
    }
}

Write-Step "DONE in $($result.elapsedSeconds)s"
$result | ConvertTo-Json -Depth 4
exit 0
