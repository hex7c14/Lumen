<#
.SYNOPSIS
  Capture the main window of a process to PNG (for unattended UI verification).

.DESCRIPTION
  Uses PrintWindow(PW_RENDERFULLCONTENT) which works for WinUI 3 / composition-rendered windows.
  The target window must be visible (not minimized).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/screenshot.ps1 -ProcessName Lumen -Output docs/screenshots/empty.png
#>
param(
    [string]$ProcessName = "Lumen",
    [string]$Output = "artifacts/screenshots/lumen.png",
    [int]$WaitSeconds = 0,
    [switch]$Kill
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not ("LumenScreenshot.Win32" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace LumenScreenshot {
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

if ($WaitSeconds -gt 0) {
    Start-Sleep -Seconds $WaitSeconds
}

$deadline = (Get-Date).AddSeconds(30)
$process = $null
while ((Get-Date) -lt $deadline) {
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
    if ($process) { break }
    Start-Sleep -Milliseconds 500
}

if (-not $process) {
    throw "No visible main window found for process '$ProcessName'."
}

$hwnd = $process.MainWindowHandle
[void][LumenScreenshot.Win32]::ShowWindow($hwnd, 9)
[void][LumenScreenshot.Win32]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 800

$rect = New-Object LumenScreenshot.Win32+RECT
$dwmResult = [LumenScreenshot.Win32]::DwmGetWindowAttribute(
    $hwnd,
    [LumenScreenshot.Win32]::DWMWA_EXTENDED_FRAME_BOUNDS,
    [ref]$rect,
    [System.Runtime.InteropServices.Marshal]::SizeOf([type][LumenScreenshot.Win32+RECT]))
if ($dwmResult -ne 0) {
    [void][LumenScreenshot.Win32]::GetWindowRect($hwnd, [ref]$rect)
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "Invalid window size: ${width}x${height}"
}

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    $ok = [LumenScreenshot.Win32]::PrintWindow($hwnd, $hdc, [LumenScreenshot.Win32]::PW_RENDERFULLCONTENT)
} finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}

if (-not $ok) {
    throw "PrintWindow failed."
}

$outputPath = [System.IO.Path]::GetFullPath($Output)
$outputDir = [System.IO.Path]::GetDirectoryName($outputPath)
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "Screenshot saved: $outputPath (${width}x${height})"

if ($Kill) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Write-Host "Process $($process.Id) stopped."
}
