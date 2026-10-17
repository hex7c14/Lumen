#requires -Version 5.1
<#
.SYNOPSIS
    Unattended UIA smoke tests for Lumen v2 (batch / CPU compatibility / compatibility cases).

.DESCRIPTION
    Scenario-driven smoke test for the Lumen WinUI 3 app. Launches the unpackaged
    exe (no MSIX, no Developer Mode), waits for the real window, asserts window
    title + key controls through `winapp ui` (UI Automation), captures screenshots,
    optionally drives batch processing / CPU mode / system-info dialog, verifies
    output files, then closes only the instance it started.

    v1 scenarios (regression):
      empty      - no image: window + empty state + v1 controls
      single     - optional -OpenImage; optional -StartUpscale (single image flow)
    v2 scenarios:
      batch-load - LUMEN_OPEN_MANY: queue panel shows the items, no processing
      batch-run  - LUMEN_OPEN_MANY + LUMEN_SAVE_DIR + LUMEN_AUTOSTART: wait for the
                   whole batch, assert summary + failure isolation + output files
      batch-cpu  - same as batch-run but LUMEN_BACKEND=cpu (fp32 CPU compatibility)
      cpu-single - LUMEN_OPEN + LUMEN_BACKEND=cpu + LUMEN_AUTOSTART
      gpu-single - LUMEN_OPEN + LUMEN_BACKEND=gpu + LUMEN_AUTOSTART (serial GPU required)
      compat-empty-models - LUMEN_MODELS_ROOT=<empty dir>: Chinese hint, no crash
      compat-bad-inputs   - valid;corrupt;valid + autostart: failure isolation
      compat-missing-input- LUMEN_OPEN_MANY=<nonexistent>: Chinese error, no crash
      system-info - open system info dialog and assert its fields

    Use -DryRun to validate parameters / fixtures / planned assertions without
    launching the app (safe while ui-dev is still editing src/Lumen.App).

.PARAMETER Scenario
    Scenario name (see above). Default: single.

.PARAMETER DryRun
    Prepare fixtures and print the planned scenario JSON; do not launch the app.

.PARAMETER AllowGpuBusy
    Allow inference scenarios even when nvidia-smi / Lumen / Lumen.Cli / python
    processes appear busy. By default inference scenarios refuse to run when the
    GPU is busy (one RTX 5060 is shared by engine-dev/model-ops/ui-dev).

.NOTES
    - Windows PowerShell 5.1, pure ASCII on purpose (UTF-8 without BOM is
      mis-decoded by PS 5.1; Chinese strings are encoded as \\uXXXX escapes).
    - winapp ui is UI Automation; it works without Developer Mode / MSIX.
    - The script only ever closes the Lumen process it launched itself.
#>
[CmdletBinding()]
param(
    [ValidateSet(
        'empty', 'single',
        'batch-load', 'batch-run', 'batch-cpu', 'batch-skip', 'cpu-single', 'gpu-single',
        'compat-empty-models', 'compat-bad-inputs', 'compat-missing-input', 'compat-no-dml',
        'system-info'
    )]
    [string]$Scenario = 'single',

    # --- v1 compatible inputs ---
    [string]$OpenImage = '',
    [switch]$StartUpscale,
    [string]$SaveImage = '',

    # --- v2 inputs ---
    [string]$OpenMany = '',                 # ';' separated input files
    [string]$SaveDir = '',                  # LUMEN_SAVE_DIR
    [ValidateSet('', 'auto', 'gpu', 'cpu')][string]$Backend = '',
    [string]$Pattern = '',                  # LUMEN_PATTERN
    [ValidateSet('', '2', '3', '4')][string]$Scale = '',
    [string]$ModelsRoot = '',               # LUMEN_MODELS_ROOT override
    [ValidateSet('', 'rename', 'overwrite', 'skip')][string]$ConflictPolicy = '',
    [string]$FixturesRoot = '',
    [switch]$UseFixtures,

    # --- execution control ---
    [string]$ExePath = '',
    [switch]$Build,
    [string]$DotnetRoot = 'C:\Users\0x7c14\dotnet10',
    [switch]$DryRun,
    [switch]$AllowGpuBusy,
    [switch]$KeepAlive,
    [switch]$NoScreenshot,
    [int]$WindowTimeoutSeconds = 60,
    [int]$UpscaleTimeoutSeconds = 600,
    [string]$ArtifactDirectory = '',
    [ValidateSet('', 'Dark', 'Light')][string]$Theme = 'Dark'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

# Decode ASCII "\uXXXX" escapes into real Unicode while keeping other regex
# escapes (\d, \s, ...) intact (keeps this file ASCII-only).
function U([string]$escaped) {
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator] {
        param($match)
        return [string][char]([Convert]::ToInt32($match.Groups[1].Value, 16))
    }
    return [regex]::Replace($escaped, '\\u([0-9A-Fa-f]{4})', $evaluator)
}

function Step([string]$message) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $message)
}

function Resolve-Winapp {
    $cmd = Get-Command winapp -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winapp.exe'
    if (Test-Path -LiteralPath $candidate) { return $candidate }
    throw "winapp CLI not found. Install Microsoft.WinAppCli or add it to PATH."
}

function Resolve-LumenExe {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "ExePath not found: $Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $bin = Join-Path $script:RepoRoot 'src\Lumen.App\bin'
    if (-not (Test-Path -LiteralPath $bin)) { return $null }
    $exe = Get-ChildItem -LiteralPath $bin -Recurse -File -Filter 'Lumen.exe' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $exe) { return $null }
    return $exe.FullName
}

function Invoke-Winapp {
    param([string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = $null
        $output = @(& $script:Winapp @Arguments 2>&1)
        $code = $global:LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($null -eq $code) { $code = 0 }
    return [pscustomobject]@{ ExitCode = [int]$code; Output = $output }
}

function Invoke-Ui {
    param([string[]]$Arguments)
    return Invoke-Winapp (@('ui') + $Arguments)
}

# ---------------------------------------------------------------------------
# UIA snapshot helpers (one inspect call is much faster than many wait-for runs)
# ---------------------------------------------------------------------------

function Get-UiSnapshot {
    param([int64]$Hwnd, [int]$Depth = 10)
    $result = Invoke-Ui @('inspect', '--json', '-d', "$Depth", '-w', "$Hwnd")
    if ($result.ExitCode -ne 0) { return $null }
    try {
        $parsed = (($result.Output -join [Environment]::NewLine) | ConvertFrom-Json)
        $window = @($parsed.windows) | Select-Object -First 1
        if (-not $window) { return $null }
    }
    catch {
        return $null
    }
    $flat = New-Object System.Collections.ArrayList
    $walk = {
        param($nodes)
        foreach ($node in $nodes) {
            if ($null -eq $node) { continue }
            [void]$flat.Add($node)
            if ($node.children) { & $walk $node.children }
        }
    }
    & $walk @($window.elements)
    return $flat
}

function Get-SnapshotTexts {
    param($Elements)
    if ($null -eq $Elements) { return @() }
    return @($Elements | Where-Object { $_.name } | ForEach-Object { [string]$_.name })
}

function Find-SnapshotElement {
    param($Elements, [string[]]$Ids, [string[]]$Names, [string]$NameRegex)
    if ($null -eq $Elements) { return [pscustomobject]@{ Found = $false; Via = 'missing'; Element = $null } }
    foreach ($id in $Ids) {
        if (-not $id) { continue }
        $el = $Elements | Where-Object { $_.automationId -eq $id } | Select-Object -First 1
        if ($el) { return [pscustomobject]@{ Found = $true; Via = 'automationId'; Element = $el } }
    }
    foreach ($name in $Names) {
        if (-not $name) { continue }
        $el = $Elements | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if ($el) { return [pscustomobject]@{ Found = $true; Via = 'name'; Element = $el } }
    }
    if ($NameRegex) {
        $el = $Elements | Where-Object { $_.name -and $_.name -match $NameRegex } | Select-Object -First 1
        if ($el) { return [pscustomobject]@{ Found = $true; Via = 'nameRegex'; Element = $el } }
    }
    return [pscustomobject]@{ Found = $false; Via = 'missing'; Element = $null }
}

function Test-SnapshotRegex {
    param($Elements, [string]$Pattern)
    foreach ($text in (Get-SnapshotTexts $Elements)) {
        if ($text -match $Pattern) { return $true }
    }
    return $false
}

# ---------------------------------------------------------------------------
# Window / screenshot helpers
# ---------------------------------------------------------------------------

function Find-LumenWindow {
    param([int]$ProcessId)
    $result = Invoke-Ui @('list-windows', '--json')
    if ($result.ExitCode -ne 0) { return $null }
    try {
        $parsed = (($result.Output -join [Environment]::NewLine) | ConvertFrom-Json)
        $windows = @($parsed)
    }
    catch {
        return $null
    }
    $match = $windows |
        Where-Object { [int64]$_.processId -eq [int64]$ProcessId -and $_.hwnd -and [int64]$_.hwnd -ne 0 } |
        Select-Object -First 1
    if ($match) { return $match }
    return $null
}

function Find-LumenWindowByName {
    # Fallback for the installer layout: the root Lumen.exe is a tiny launcher that
    # exits immediately after starting runtime\Lumen.exe. Find a window in any
    # process named Lumen instead of the (already exited) launcher PID.
    $processes = @(Get-Process -Name 'Lumen' -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        $process.Refresh()
        if ($process.HasExited) { continue }
        $window = Find-LumenWindow -ProcessId $process.Id
        if ($window) { return $window }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [pscustomobject]@{
                hwnd = [int64]$process.MainWindowHandle; processId = $process.Id
                title = $process.MainWindowTitle; className = ''; width = 0; height = 0
            }
        }
    }
    return $null
}

function Wait-LumenWindow {
    param([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $launcherExited = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $window = Find-LumenWindow -ProcessId $Process.Id
        if ($window) { return $window }
        if (-not $launcherExited -and $Process.HasExited) {
            # Installer layout: root launcher exits after spawning runtime\Lumen.exe.
            $launcherExited = $true
        }
        if ($launcherExited) {
            $window = Find-LumenWindowByName
            if ($window) { return $window }
            continue
        }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [pscustomobject]@{
                hwnd = [int64]$Process.MainWindowHandle; processId = $Process.Id
                title = $Process.MainWindowTitle; className = ''; width = 0; height = 0
            }
        }
    }
    throw "No Lumen window appeared for PID $($Process.Id) within ${TimeoutSeconds}s."
}

if (-not ('LumenSmokeWin32' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class LumenSmokeWin32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);

    public const uint WM_CLOSE = 0x0010;
    public const uint PW_RENDERFULLCONTENT = 2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
}
"@ | Out-Null
}

function Save-WindowScreenshot {
    param([int64]$Hwnd, [string]$Path)
    $handle = [IntPtr]$Hwnd
    if (-not [LumenSmokeWin32]::IsWindow($handle)) { throw "Window handle $Hwnd is no longer valid." }
    [void][LumenSmokeWin32]::ShowWindow($handle, 9)
    [void][LumenSmokeWin32]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 700

    $rect = New-Object LumenSmokeWin32+RECT
    $dwm = [LumenSmokeWin32]::DwmGetWindowAttribute(
        $handle, [LumenSmokeWin32]::DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$rect,
        [System.Runtime.InteropServices.Marshal]::SizeOf([type][LumenSmokeWin32+RECT]))
    if ($dwm -ne 0) { [void][LumenSmokeWin32]::GetWindowRect($handle, [ref]$rect) }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 200 -or $height -lt 150) { throw "Unexpected window size ${width}x${height}." }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try { $ok = [LumenSmokeWin32]::PrintWindow($handle, $hdc, [LumenSmokeWin32]::PW_RENDERFULLCONTENT) }
        finally { $graphics.ReleaseHdc($hdc) }
        if (-not $ok) {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
        }
    }
    finally { $graphics.Dispose() }

    $colors = @{}
    $stepX = [Math]::Max(1, [int]($width / 40))
    $stepY = [Math]::Max(1, [int]($height / 40))
    for ($y = 0; $y -lt $height; $y += $stepY) {
        for ($x = 0; $x -lt $width; $x += $stepX) { $colors[$bitmap.GetPixel($x, $y).ToArgb()] = $true }
    }
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return [pscustomobject]@{
        Path = $Path; Width = $width; Height = $height
        DistinctColors = $colors.Count; LooksBlank = ($colors.Count -lt 8)
    }
}

# ---------------------------------------------------------------------------
# Fixture + image helpers
# ---------------------------------------------------------------------------

function Get-ImageSize {
    param([string]$Path)
    try {
        $image = [System.Drawing.Image]::FromFile($Path)
        try { return [pscustomobject]@{ Width = $image.Width; Height = $image.Height } }
        finally { $image.Dispose() }
    }
    catch { return $null }
}

function Get-FixtureSourceDir {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "FixturesRoot not found: $Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $candidates = @(
        (Join-Path $script:RepoRoot 'assets\samples\anime'),
        (Join-Path $script:RepoRoot 'assets\samples'),
        (Join-Path $script:RepoRoot 'tools\model-pipeline\samples\anime'),
        (Join-Path $script:RepoRoot 'spike\winui-smoke\Assets')
    )
    foreach ($dir in $candidates) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $images = @(Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|bmp)$' })
        foreach ($image in $images) {
            if (Get-ImageSize $image.FullName) { return $dir }
        }
    }
    throw "No decodable fixture images found. Pass -FixturesRoot <dir> or -OpenMany <files>."
}

function New-Fixtures {
    param([string]$Directory, [int]$ValidCount, [switch]$WithCorrupt)
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $sourceDir = Get-FixtureSourceDir -Explicit $FixturesRoot
    $images = @(Get-ChildItem -LiteralPath $sourceDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match '^\.(png|jpg|jpeg)$' } |
        Sort-Object Length)
    $decodable = @()
    foreach ($image in $images) {
        if (Get-ImageSize $image.FullName) { $decodable += $image }
        if ($decodable.Count -ge [Math]::Max($ValidCount, 3)) { break }
    }
    if ($decodable.Count -eq 0) { throw "No decodable fixture images under $sourceDir." }
    $images = $decodable

    $valid = @()
    for ($i = 0; $i -lt $ValidCount; $i++) {
        $source = $images[$i % $images.Count]
        $target = Join-Path $Directory ("fixture-{0}.png" -f ($i + 1))
        # Normalize fixtures: decode (PNG/JPEG only), downscale to max 512 px long edge
        # and save as PNG. Keeps CPU/GPU smoke runs fast and avoids GDI+-unsupported
        # containers (e.g. the model-pipeline anime *.jpg files that are really WebP).
        $image = $null
        $bitmap = $null
        $graphics = $null
        try {
            $image = [System.Drawing.Image]::FromFile($source.FullName)
            $longEdge = [Math]::Max($image.Width, $image.Height)
            $ratio = 1.0
            if ($longEdge -gt 512) { $ratio = 512.0 / $longEdge }
            $width = [Math]::Max(1, [int][Math]::Round($image.Width * $ratio))
            $height = [Math]::Max(1, [int][Math]::Round($image.Height * $ratio))
            $bitmap = New-Object System.Drawing.Bitmap($width, $height)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($image, 0, 0, $width, $height)
            $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            if ($graphics) { $graphics.Dispose() }
            if ($bitmap) { $bitmap.Dispose() }
            if ($image) { $image.Dispose() }
        }
        $valid += $target
    }
    $corrupt = $null
    if ($WithCorrupt) {
        $corrupt = Join-Path $Directory 'corrupt.png'
        [IO.File]::WriteAllBytes($corrupt, [Text.Encoding]::ASCII.GetBytes('this is not a valid PNG file'))
    }
    $emptyModels = Join-Path $Directory 'empty-models'
    New-Item -ItemType Directory -Force -Path $emptyModels | Out-Null
    return [pscustomobject]@{
        Directory = $Directory
        SourceDirectory = $sourceDir
        Valid = $valid
        Corrupt = $corrupt
        EmptyModels = $emptyModels
    }
}

# ---------------------------------------------------------------------------
# GPU serialization guard
# ---------------------------------------------------------------------------

function Get-GpuStatus {
    $status = [ordered]@{
        NvidiaSmi = $null; Utilization = $null; MemoryUsedMiB = $null; MemoryTotalMiB = $null
        BusyProcesses = @(); Busy = $false; Detail = ''
    }
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($smi) {
        $status.NvidiaSmi = $smi.Source
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $line = @(& $smi.Source --query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits 2>$null) |
                Select-Object -First 1
            if ($line) {
                $parts = [string]$line -split ','
                if ($parts.Count -ge 3) {
                    $status.Utilization = [int]$parts[0].Trim()
                    $status.MemoryUsedMiB = [int]$parts[1].Trim()
                    $status.MemoryTotalMiB = [int]$parts[2].Trim()
                }
            }
        }
        finally { $ErrorActionPreference = $previous }
    }
    $busy = @()
    foreach ($name in @('Lumen', 'Lumen.Cli', 'python', 'python3')) {
        $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
        foreach ($p in $procs) { $busy += ("{0}({1})" -f $p.ProcessName, $p.Id) }
    }
    $status.BusyProcesses = $busy
    if ($null -ne $status.Utilization -and $status.Utilization -ge 30) { $status.Busy = $true }
    if ($busy.Count -gt 0) { $status.Busy = $true }
    $status.Detail = ("nvidia-smi={0}; util={1}%; mem={2}/{3} MiB; other-processes={4}" -f `
        $status.NvidiaSmi, $status.Utilization, $status.MemoryUsedMiB, $status.MemoryTotalMiB, ($busy -join ','))
    return $status
}

# ---------------------------------------------------------------------------
# Batch output verification
# ---------------------------------------------------------------------------

function Test-BatchOutputs {
    param([string]$Directory, [string[]]$InputPaths, [int]$Scale)
    $verification = [ordered]@{
        directory = $Directory
        expectedCount = 0
        actualCount = 0
        files = @()
        dimensionsOk = $null
        notes = @()
    }
    if (-not (Test-Path -LiteralPath $Directory)) {
        $verification.notes += 'Output directory does not exist.'
        return $verification
    }
    $validInputs = @()
    foreach ($input in $InputPaths) {
        if (-not $input) { continue }
        if (-not (Test-Path -LiteralPath $input)) { continue }
        if (Get-ImageSize $input) { $validInputs += $input }
    }
    $verification.expectedCount = $validInputs.Count
    $files = @(Get-ChildItem -LiteralPath $Directory -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|webp|bmp)$' })
    $verification.actualCount = $files.Count
    $verification.files = @($files | ForEach-Object { $_.FullName })
    $expectedDims = @()
    foreach ($input in $validInputs) {
        $size = Get-ImageSize $input
        if ($size) { $expectedDims += ("{0}x{1}" -f ($size.Width * $Scale), ($size.Height * $Scale)) }
    }
    $actualDims = @()
    foreach ($file in $files) {
        $size = Get-ImageSize $file.FullName
        if ($size) { $actualDims += ("{0}x{1}" -f $size.Width, $size.Height) }
    }
    if ($expectedDims.Count -gt 0 -and $actualDims.Count -eq $expectedDims.Count) {
        $sortedExpected = @($expectedDims | Sort-Object)
        $sortedActual = @($actualDims | Sort-Object)
        $verification.dimensionsOk = (($sortedExpected -join ',') -eq ($sortedActual -join ','))
        if (-not $verification.dimensionsOk) {
            $verification.notes += ("expected dims [{0}] but got [{1}]" -f ($sortedExpected -join ','), ($sortedActual -join ','))
        }
    }
    else {
        $verification.notes += 'Could not compare dimensions (missing images or count mismatch).'
    }
    if ($verification.actualCount -ne $verification.expectedCount) {
        $verification.notes += ("output count {0} != expected {1}" -f $verification.actualCount, $verification.expectedCount)
    }
    return $verification
}

# ---------------------------------------------------------------------------
# Scenario definitions
# ---------------------------------------------------------------------------
# Action:
#   ''            - just load and assert controls
#   'start-single'- click start button, wait single completion
#   'wait-single' - autostart env already asked the app to run; just wait
#   'start-batch' - click start-all, wait batch completion
#   'wait-batch'  - autostart env already asked the app to run; just wait
#   'system-info' - open system info dialog
$scenarioTable = @{
    'empty' = @{
        Description = 'v1 regression: empty state, window + v1 controls only'
        Action = ''; Queue = $false; Inference = $false
    }
    'single' = @{
        Description = 'v1 regression: optional single image load / single upscale'
        Action = ''; Queue = $false; Inference = $false
    }
    'batch-load' = @{
        Description = 'v2: LUMEN_OPEN_MANY fills the queue, no processing'
        Action = ''; Queue = $true; Inference = $false
    }
    'batch-run' = @{
        Description = 'v2: batch autostart + save dir + corrupt item failure isolation'
        Action = 'wait-batch'; Queue = $true; Inference = $true
    }
    'batch-cpu' = @{
        Description = 'v2: batch autostart on CPU compatibility mode (LUMEN_BACKEND=cpu)'
        Action = 'wait-batch'; Queue = $true; Inference = $false
    }
    'batch-skip' = @{
        Description = 'v2: conflict policy Skip keeps an existing output file untouched and reports skipped'
        Action = 'wait-batch'; Queue = $true; Inference = $false
    }
    'cpu-single' = @{
        Description = 'v2: single image on CPU compatibility mode'
        Action = 'wait-single'; Queue = $false; Inference = $false
    }
    'gpu-single' = @{
        Description = 'v2: single image with GPU backend (serial GPU required)'
        Action = 'wait-single'; Queue = $false; Inference = $true
    }
    'compat-empty-models' = @{
        Description = 'v2 compatibility: LUMEN_MODELS_ROOT points to an empty directory'
        Action = ''; Queue = $false; Inference = $false
    }
    'compat-bad-inputs' = @{
        Description = 'v2 compatibility: queue with a corrupt image must not crash'
        Action = 'wait-batch'; Queue = $true; Inference = $true
    }
    'compat-missing-input' = @{
        Description = 'v2 compatibility: nonexistent input path shows a Chinese error'
        Action = ''; Queue = $false; Inference = $false
    }
    'compat-no-dml' = @{
        Description = 'v2 compatibility: LUMEN_FORCE_NO_DML=1 makes auto mode fall back to CPU with a one-time hint'
        Action = ''; Queue = $false; Inference = $false
    }
    'system-info' = @{
        Description = 'v2: system info dialog contents'
        Action = 'system-info'; Queue = $false; Inference = $false
    }
}

# v1 contract (regression checks; adapted to the v2 UI layout: iteration-1 toolbar
# export/100% buttons were replaced by the batch flow; zoom/fit/compare moved to the
# canvas toolbar and only exist when an image is loaded -> LoadedOnly).
$contractV1 = @(
    @{ Key = 'BtnOpenImage'; Ids = @('BtnAddImages', 'BtnOpenImage'); Names = @((U '\u6dfb\u52a0\u56fe\u7247'), (U '\u6253\u5f00\u56fe\u7247')); Required = $true },
    @{ Key = 'BtnExport'; Ids = @('BtnExport'); Names = @((U '\u5bfc\u51fa')); Required = $false; Note = 'removed in v2: batch flow auto-saves results' },
    @{ Key = 'BtnFit'; Ids = @('BtnFitCanvas', 'BtnFit'); Names = @((U '\u9002\u5e94\u7a97\u53e3')); Required = $true; LoadedOnly = $true },
    @{ Key = 'BtnZoom100'; Ids = @('BtnZoom100'); Names = @((U '\u7f29\u653e\u5230 100%')); Required = $false; Note = 'removed in v2 canvas toolbar' },
    @{ Key = 'BtnCompare'; Ids = @('BtnCompare', 'CompareToggle'); Names = @((U '\u5bf9\u6bd4\u6a21\u5f0f')); Required = $true; LoadedOnly = $true },
    @{ Key = 'BtnStart'; Ids = @('BtnStartAll', 'BtnStart'); Names = @((U '\u5f00\u59cb\u8d85\u5206'), (U '\u5f00\u59cb\u5904\u7406\u5168\u90e8'), (U '\u5f00\u59cb\u8d85\u5206\u6216\u53d6\u6d88\u5904\u7406')); Required = $true },
    @{ Key = 'BtnZoomIn'; Ids = @('BtnZoomIn'); Names = @((U '\u653e\u5927')); Required = $true; LoadedOnly = $true },
    @{ Key = 'BtnZoomOut'; Ids = @('BtnZoomOut'); Names = @((U '\u7f29\u5c0f')); Required = $true; LoadedOnly = $true },
    @{ Key = 'BtnCollapsePanel'; Ids = @('BtnCollapsePanel'); Names = @((U '\u6298\u53e0\u53c2\u6570\u9762\u677f')); Required = $true },
    @{ Key = 'CmbModel'; Ids = @('CmbModel'); Names = @((U '\u6a21\u578b')); Required = $true },
    @{ Key = 'RadioScale'; Ids = @('RadioScale'); Names = @((U '\u8f93\u51fa\u500d\u7387')); Required = $true },
    @{ Key = 'CmbFormat'; Ids = @('CmbFormat'); Names = @((U '\u8f93\u51fa\u683c\u5f0f')); Required = $true },
    @{ Key = 'TogglePreserveAlpha'; Ids = @('TogglePreserveAlpha'); Names = @((U '\u4fdd\u7559\u900f\u660e\u901a\u9053')); Required = $true },
    @{ Key = 'ExpanderAdvanced'; Ids = @('ExpanderAdvanced'); Names = @((U '\u9ad8\u7ea7')); Required = $true },
    @{ Key = 'TxtStatus'; Ids = @('TxtStatus'); Names = @(); Required = $true },
    @{ Key = 'TxtDimensions'; Ids = @('TxtDimensions'); Names = @((U '\u672a\u8f7d\u5165\u56fe\u7247')); NameRegex = (U '\u2192'); Required = $true },
    @{ Key = 'BtnCancel'; Ids = @('BtnCancel'); Names = @((U '\u53d6\u6d88\u5904\u7406')); Required = $false },
    @{ Key = 'CanvasViewer'; Ids = @('CanvasViewer'); Names = @(); Required = $false }
)

# v2 additions (iteration 2)
# RequiredFor: scenario names where the control must exist (empty = always when v2 scenario).
$contractV2 = @(
    @{ Key = 'BtnAddImages'; Ids = @('BtnAddImages'); Names = @((U '\u6dfb\u52a0\u56fe\u7247')); RequiredFor = @('batch-load', 'batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'BtnAddFolder'; Ids = @('BtnAddFolder'); Names = @((U '\u6dfb\u52a0\u6587\u4ef6\u5939')); RequiredFor = @('batch-load', 'batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'BtnClearQueue'; Ids = @('BtnClearQueue'); Names = @((U '\u6e05\u7a7a\u961f\u5217')); RequiredFor = @('batch-load', 'batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'QueueList'; Ids = @('QueueList'); Names = @(); RequiredFor = @('batch-load', 'batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'TxtQueueSummary'; Ids = @('TxtQueueSummary'); Names = @(); NameRegex = (U '\u5f20'); RequiredFor = @('batch-load', 'batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'CmbComputeMode'; Ids = @('CmbComputeMode', 'RadioComputeMode', 'SegComputeMode'); Names = @((U '\u5904\u7406\u6a21\u5f0f')); RequiredFor = @('batch-cpu', 'cpu-single', 'gpu-single') },
    @{ Key = 'TxtComputeModeChip'; Ids = @('TxtComputeModeChip'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtComputeModeHint'; Ids = @('TxtComputeModeHint'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtOutputDirectory'; Ids = @('TxtOutputDirectory'); Names = @(); RequiredFor = @('batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'RadioOutputDirectory'; Ids = @('RadioOutputDirectory'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnPickOutputDir'; Ids = @('BtnPickOutputDir', 'BtnPickOutputDirectory'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnClearOutputDir'; Ids = @('BtnClearOutputDir'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtFileNamePattern'; Ids = @('TxtFileNamePattern'); Names = @(); RequiredFor = @('batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'CmbConflictPolicy'; Ids = @('CmbConflictPolicy'); Names = @(); RequiredFor = @('batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs') },
    @{ Key = 'ToggleOpenOutputFolder'; Ids = @('ToggleOpenOutputFolder'); Names = @(); RequiredFor = @() },
    @{ Key = 'ToggleShowGeneralModels'; Ids = @('ToggleShowGeneralModels'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnSystemInfo'; Ids = @('BtnSystemInfo'); Names = @(); RequiredFor = @('system-info') },
    @{ Key = 'TxtBatchProgress'; Ids = @('TxtBatchProgress'); Names = @(); NameRegex = (U '\u7b2c\s*\d+\s*/\s*\d+\s*\u5f20'); RequiredFor = @() },
    @{ Key = 'TxtBackend'; Ids = @('TxtBackend'); Names = @(); RequiredFor = @('batch-cpu', 'cpu-single', 'gpu-single') },
    @{ Key = 'InfoBarStatus'; Ids = @('InfoBarStatus'); Names = @(); RequiredFor = @('compat-missing-input', 'compat-empty-models') },
    @{ Key = 'DlgSystemInfo'; Ids = @('DlgSystemInfo'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtSysOs'; Ids = @('TxtSysOs'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtSysCpu'; Ids = @('TxtSysCpu'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtSysGpu'; Ids = @('TxtSysGpu'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtSysDirectMl'; Ids = @('TxtSysDirectMl'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtSysModels'; Ids = @('TxtSysModels'); Names = @(); RequiredFor = @() },
    @{ Key = 'TxtSysLogPath'; Ids = @('TxtSysLogPath'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnOpenLogFolder'; Ids = @('BtnOpenLogFolder'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnCopySystemInfo'; Ids = @('BtnCopySystemInfo'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnOpenModelsFolder'; Ids = @('BtnOpenModelsFolder'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnCollapseQueue'; Ids = @('BtnCollapseQueue'); Names = @(); RequiredFor = @() },
    @{ Key = 'BtnExpandQueue'; Ids = @('BtnExpandQueue'); Names = @(); RequiredFor = @() }
)

# ---------------------------------------------------------------------------
# Paths / preflight
# ---------------------------------------------------------------------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:RepoRoot = Split-Path -Parent $scriptDir
$script:Winapp = Resolve-Winapp
$startedAt = Get-Date

$scenarioInfo = $scenarioTable[$Scenario]
if ($null -eq $scenarioInfo) { throw "Unknown scenario '$Scenario'." }

if (-not $ArtifactDirectory) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ArtifactDirectory = Join-Path $script:RepoRoot ("artifacts\ui-smoke\v2-{0}-{1}" -f $Scenario, $stamp)
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null

$exe = Resolve-LumenExe -Explicit $ExePath

$result = [ordered]@{
    ok = $false
    scenario = $Scenario
    scenarioDescription = $scenarioInfo.Description
    startedAt = $startedAt.ToString('o')
    exePath = $exe
    artifactDirectory = $ArtifactDirectory
    dryRun = [bool]$DryRun
    pid = 0
    hwnd = 0
    windowTitle = ''
    env = [ordered]@{}
    gpu = $null
    fixtures = $null
    assertions = @()
    missingAutomationIds = @()
    fallbackControls = @()
    outputVerification = $null
    skipSentinel = $null
    screenshots = @()
    phases = [ordered]@{}
    notes = @()
}

# ---------------------------------------------------------------------------
# Optional build
# ---------------------------------------------------------------------------
if ($Build) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        $candidate = Join-Path $DotnetRoot 'dotnet.exe'
        if (-not (Test-Path -LiteralPath $candidate)) { throw "dotnet not found (checked PATH and $candidate)." }
        $dotnet = $candidate
    }
    $dotnetExe = $dotnet
    if ($dotnet -isnot [string]) { $dotnetExe = $dotnet.Source }
    Step "building src/Lumen.App ..."
    $buildOutput = & $dotnetExe build (Join-Path $script:RepoRoot 'src\Lumen.App\Lumen.App.csproj') -c Debug -p:Platform=x64 -v minimal 2>&1
    $buildExit = $LASTEXITCODE
    $buildOutput | ForEach-Object { Write-Host "  $_" }
    if ($buildExit -ne 0) { throw "dotnet build failed with exit code $buildExit." }
    $exe = Resolve-LumenExe -Explicit ''
    $result.exePath = $exe
}

# ---------------------------------------------------------------------------
# Scenario fixtures + expected inputs
# ---------------------------------------------------------------------------
$fixtures = $null
$openManyList = @()
$fixtureDir = Join-Path $ArtifactDirectory 'fixtures'

switch ($Scenario) {
    'batch-load' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 2 -WithCorrupt
        if ($OpenMany) { $openManyList = @($OpenMany -split ';' | Where-Object { $_ }) }
        else { $openManyList = @($fixtures.Valid) + @($fixtures.Corrupt) }
    }
    'batch-run' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 2 -WithCorrupt
        if ($OpenMany) { $openManyList = @($OpenMany -split ';' | Where-Object { $_ }) }
        else { $openManyList = @($fixtures.Valid) + @($fixtures.Corrupt) }
    }
    'batch-cpu' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 2
        if ($OpenMany) { $openManyList = @($OpenMany -split ';' | Where-Object { $_ }) }
        else { $openManyList = @($fixtures.Valid) }
    }
    'batch-skip' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 1
        if ($OpenMany) { $openManyList = @($OpenMany -split ';' | Where-Object { $_ }) }
        else { $openManyList = @($fixtures.Valid) }
    }
    'compat-bad-inputs' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 2 -WithCorrupt
        if ($OpenMany) { $openManyList = @($OpenMany -split ';' | Where-Object { $_ }) }
        else { $openManyList = @($fixtures.Valid[0], $fixtures.Corrupt, $fixtures.Valid[1]) }
    }
    'compat-missing-input' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 1
        $missing = Join-Path $fixtureDir 'does-not-exist.png'
        $openManyList = @($missing)
    }
    'compat-empty-models' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 1
    }
    'system-info' {
        $fixtures = New-Fixtures -Directory $fixtureDir -ValidCount 1
    }
    default {
        if ($OpenImage) { $openManyList = @($OpenImage) }
    }
}
if ($fixtures) { $result.fixtures = $fixtures }

# Resolve explicit v2 inputs that were not auto-generated.
if ($OpenMany -and $openManyList.Count -eq 0) {
    $openManyList = @($OpenMany -split ';' | Where-Object { $_ })
}

# ---------------------------------------------------------------------------
# Environment variables
# ---------------------------------------------------------------------------
$envMap = [ordered]@{}
if ($Theme) { $envMap['LUMEN_THEME'] = $Theme }
if ($Scenario -in @('single', 'gpu-single', 'cpu-single', 'empty', 'system-info') -and $OpenImage) {
    $envMap['LUMEN_OPEN'] = [IO.Path]::GetFullPath($OpenImage)
}
if ($openManyList.Count -gt 0) { $envMap['LUMEN_OPEN_MANY'] = ($openManyList -join ';') }
if ($SaveImage) { $envMap['LUMEN_SAVE_TO'] = [IO.Path]::GetFullPath($SaveImage) }
if ($Scale) { $envMap['LUMEN_SCALE'] = $Scale }
if ($Pattern) { $envMap['LUMEN_PATTERN'] = $Pattern }
if ($ModelsRoot) { $envMap['LUMEN_MODELS_ROOT'] = [IO.Path]::GetFullPath($ModelsRoot) }
if ($ConflictPolicy) { $envMap['LUMEN_CONFLICT'] = $ConflictPolicy }
if ($Backend) { $envMap['LUMEN_BACKEND'] = $Backend }

switch ($Scenario) {
    'batch-run' {
        if (-not $SaveDir) { $SaveDir = Join-Path $ArtifactDirectory 'out' }
        $SaveDir = [IO.Path]::GetFullPath($SaveDir)
        New-Item -ItemType Directory -Force -Path $SaveDir | Out-Null
        $envMap['LUMEN_SAVE_DIR'] = $SaveDir
        $envMap['LUMEN_AUTOSTART'] = '1'
        if (-not $Backend) { $envMap['LUMEN_BACKEND'] = 'auto' }
    }
    'batch-cpu' {
        if (-not $SaveDir) { $SaveDir = Join-Path $ArtifactDirectory 'out' }
        $SaveDir = [IO.Path]::GetFullPath($SaveDir)
        New-Item -ItemType Directory -Force -Path $SaveDir | Out-Null
        $envMap['LUMEN_SAVE_DIR'] = $SaveDir
        $envMap['LUMEN_AUTOSTART'] = '1'
        $envMap['LUMEN_BACKEND'] = 'cpu'
    }
    'batch-skip' {
        if (-not $SaveDir) { $SaveDir = Join-Path $ArtifactDirectory 'out' }
        $SaveDir = [IO.Path]::GetFullPath($SaveDir)
        New-Item -ItemType Directory -Force -Path $SaveDir | Out-Null
        $envMap['LUMEN_SAVE_DIR'] = $SaveDir
        $envMap['LUMEN_AUTOSTART'] = '1'
        $envMap['LUMEN_BACKEND'] = 'cpu'
        $envMap['LUMEN_CONFLICT'] = 'skip'
        if (-not $Pattern) { $envMap['LUMEN_PATTERN'] = '{name}_smoke' }
    }
    'compat-bad-inputs' {
        if (-not $SaveDir) { $SaveDir = Join-Path $ArtifactDirectory 'out' }
        $SaveDir = [IO.Path]::GetFullPath($SaveDir)
        New-Item -ItemType Directory -Force -Path $SaveDir | Out-Null
        $envMap['LUMEN_SAVE_DIR'] = $SaveDir
        $envMap['LUMEN_AUTOSTART'] = '1'
        if (-not $Backend) { $envMap['LUMEN_BACKEND'] = 'gpu' }
    }
    'compat-no-dml' {
        $envMap['LUMEN_FORCE_NO_DML'] = '1'
        if (-not $Backend) { $envMap['LUMEN_BACKEND'] = 'auto' }
    }
    'compat-empty-models' {
        $envMap['LUMEN_MODELS_ROOT'] = $fixtures.EmptyModels
    }
    'cpu-single' {
        $envMap['LUMEN_AUTOSTART'] = '1'
        $envMap['LUMEN_BACKEND'] = 'cpu'
    }
    'gpu-single' {
        $envMap['LUMEN_AUTOSTART'] = '1'
        $envMap['LUMEN_BACKEND'] = 'gpu'
    }
    'single' {
        if ($SaveImage) { $envMap['LUMEN_AUTOSTART'] = '1' }
    }
}
foreach ($key in $envMap.Keys) { $result.env[$key] = $envMap[$key] }

# Action resolution for v1 compatibility.
$action = [string]$scenarioInfo.Action
if ($Scenario -eq 'single') {
    if ($StartUpscale) { $action = 'start-single' }
    elseif ($SaveImage) { $action = 'wait-single' }
}

# Expected outputs (decodable input files only).
$expectedOutputs = @()
foreach ($input in $openManyList) {
    if ($input -and (Test-Path -LiteralPath $input) -and (Get-ImageSize $input)) { $expectedOutputs += $input }
}
$scaleValue = 4
if ($Scale) { $scaleValue = [int]$Scale }

# batch-skip: pre-create the expected output path so the Skip conflict policy has
# something to skip. Expected semantics (Lead decision 2026-10-17): Skipped items
# report the *planned* OutputPath but must NOT write/overwrite the file.
$skipSentinelPath = $null
$skipSentinelHash = $null
if ($Scenario -eq 'batch-skip' -and $SaveDir -and $openManyList.Count -gt 0) {
    $skipPattern = '{name}_smoke'
    if ($Pattern) { $skipPattern = $Pattern }
    $skipName = [IO.Path]::GetFileNameWithoutExtension($openManyList[0])
    $skipName = $skipPattern.Replace('{name}', $skipName)
    $skipName = $skipName.Replace('{scale}', [string]$scaleValue)
    $skipName = ($skipName -replace '[\\/:*?"<>|]', '_')
    if (-not $skipName.ToLowerInvariant().EndsWith('.png')) { $skipName = $skipName + '.png' }
    $skipSentinelPath = Join-Path $SaveDir $skipName
    [IO.File]::WriteAllBytes($skipSentinelPath, [Text.Encoding]::ASCII.GetBytes('LUMEN_SMOKE_SKIP_SENTINEL'))
    $skipSentinelHash = (Get-FileHash -LiteralPath $skipSentinelPath -Algorithm SHA256).Hash
    $result.skipSentinel = [ordered]@{
        path = $skipSentinelPath
        hashBefore = $skipSentinelHash
        expectedStatus = 'Skipped (OutputPath reported, file untouched)'
    }
    Step ("batch-skip sentinel created: {0}" -f $skipSentinelPath)
}

# ---------------------------------------------------------------------------
# GPU guard for inference scenarios that may use DirectML
# ---------------------------------------------------------------------------
$usesGpu = ([bool]$scenarioInfo.Inference -and $envMap['LUMEN_BACKEND'] -ne 'cpu')
if ($usesGpu -and -not $DryRun) {
    $gpu = Get-GpuStatus
    $result.gpu = $gpu
    if ($gpu.Busy -and -not $AllowGpuBusy) {
        $result.notes += ("GPU looks busy ({0}). Refusing inference scenario to keep tests serial; " +
            "wait for engine-dev/model-ops or pass -AllowGpuBusy." -f $gpu.Detail)
        $result.phases['gpu-guard'] = 'refused'
        Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'result.json') -Value ($result | ConvertTo-Json -Depth 10) -Encoding UTF8
        Write-Host "GPU BUSY: $($gpu.Detail)"
        Write-Host "Refused inference scenario '$Scenario'. Result JSON written to $ArtifactDirectory"
        exit 3
    }
}

# ---------------------------------------------------------------------------
# Dry run
# ---------------------------------------------------------------------------
if ($DryRun) {
    $firstId = 'n/a'
    $planGpu = 'not-used'
    if ($usesGpu) {
        $dryGpu = Get-GpuStatus
        $planGpu = ("busy={0}; util={1}%; procs={2}; detail={3}" -f $dryGpu.Busy, $dryGpu.Utilization, ($dryGpu.BusyProcesses -join ','), $dryGpu.Detail)
    }
    $plannedChecks = @()
    $plannedChecks += 'window title = Lumen'
    $plannedChecks += 'v1 controls: 18 (see contractV1)'
    if ($scenarioInfo.Queue) { $plannedChecks += 'v2 queue controls + queue item names (filename, status)' }
    if ($action -in @('start-batch', 'wait-batch')) {
        $plannedChecks += 'wait batch completion: summary "success N / failed M" + failure isolation'
        $plannedChecks += 'verify output dir: file count + dimensions'
    }
    if ($action -in @('start-single', 'wait-single')) { $plannedChecks += 'wait single completion + backend label' }
    if ($action -eq 'system-info') { $plannedChecks += 'open system info dialog and assert OS/CPU/GPU/DirectML/models/log fields' }
    if ($Scenario -eq 'compat-empty-models') { $plannedChecks += 'assert Chinese model hint, start disabled, no crash' }
    if ($Scenario -eq 'compat-missing-input') { $plannedChecks += 'assert Chinese missing-file error, no crash' }
    if ($Scenario -eq 'compat-no-dml') {
        $plannedChecks += 'assert one-time CPU fallback hint (auto + LUMEN_FORCE_NO_DML=1)'
        $plannedChecks += 'assert TxtBackend shows CPU'
    }
    if ($Scenario -eq 'batch-load') { $plannedChecks += 'assert queue count text + each input file name listed' }
    if ($Scenario -eq 'batch-skip') {
        $plannedChecks += 'skip policy: queue item reports skipped, summary contains skipped N'
        $plannedChecks += 'pre-created output file must stay byte-identical (no write/overwrite)'
    }
    $plan = [ordered]@{
        scenario = $Scenario
        description = $scenarioInfo.Description
        action = $action
        queueScenario = [bool]$scenarioInfo.Queue
        inference = [bool]$scenarioInfo.Inference
        usesGpu = $usesGpu
        skipPolicy = ($Scenario -eq 'batch-skip')
        skipSentinelPath = $skipSentinelPath
        exePath = $exe
        artifactDirectory = $ArtifactDirectory
        env = $result.env
        fixtures = $result.fixtures
        expectedOutputCount = $expectedOutputs.Count
        expectedOutputs = $expectedOutputs
        gpu = $planGpu
        plannedChecks = $plannedChecks
    }
    $planPath = Join-Path $ArtifactDirectory 'plan.json'
    Set-Content -LiteralPath $planPath -Value ($plan | ConvertTo-Json -Depth 10) -Encoding UTF8
    Write-Host ''
    Write-Host '==== ui-smoke v2 DRY RUN ===='
    Write-Host ("scenario : {0} ({1})" -f $Scenario, $scenarioInfo.Description)
    Write-Host ("action   : '{0}'  queue={1}  inference={2}  usesGpu={3}" -f $action, $scenarioInfo.Queue, $scenarioInfo.Inference, $usesGpu)
    Write-Host ("exe      : {0}" -f $exe)
    Write-Host ("artifacts: {0}" -f $ArtifactDirectory)
    $envText = @()
    foreach ($key in $result.env.Keys) { $envText += ("{0}={1}" -f $key, $result.env[$key]) }
    Write-Host ("env      : {0}" -f ($envText -join '; '))
    if ($fixtures) {
        Write-Host ("fixtures : source={0} valid={1} corrupt={2} emptyModels={3}" -f $fixtures.SourceDirectory, ($fixtures.Valid -join ','), $fixtures.Corrupt, $fixtures.EmptyModels)
    }
    Write-Host ("gpu      : {0}" -f $planGpu)
    Write-Host ("checks   : {0}" -f ($plannedChecks -join ' | '))
    Write-Host ("plan json: {0}" -f $planPath)
    exit 0
}

if (-not $exe) { throw "Lumen.exe not found under src/Lumen.App/bin. Build the app first (or pass -Build/-ExePath)." }

# ---------------------------------------------------------------------------
# Launch + assertions
# ---------------------------------------------------------------------------
$previousEnv = @{}
foreach ($key in @('LUMEN_OPEN', 'LUMEN_OPEN_MANY', 'LUMEN_THEME', 'LUMEN_AUTOSTART', 'LUMEN_SAVE_TO',
        'LUMEN_SAVE_DIR', 'LUMEN_BACKEND', 'LUMEN_PATTERN', 'LUMEN_SCALE', 'LUMEN_MODELS_ROOT', 'LUMEN_CONFLICT', 'LUMEN_FORCE_NO_DML')) {
    $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key)
}
$fatalError = $false
$process = $null

function Invoke-Control {
    param([string[]]$Ids, [string[]]$Names)
    foreach ($id in $Ids) {
        if (-not $id) { continue }
        $invoke = Invoke-Ui @('invoke', $id, '-w', "$($result.hwnd)", '--json')
        if ($invoke.ExitCode -eq 0) { return $id }
    }
    foreach ($name in $Names) {
        if (-not $name) { continue }
        $invoke = Invoke-Ui @('invoke', $name, '-w', "$($result.hwnd)", '--json')
        if ($invoke.ExitCode -eq 0) { return $name }
    }
    return $null
}

function Wait-Completion {
    param([string]$Kind, [int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    if ($Kind -eq 'batch') { $doneRegex = (U '\u6210\u529f\s*\d+\s*/\s*\u5931\u8d25\s*\d+') }
    else { $doneRegex = (U '\u5904\u7406\u5b8c\u6210|\u6279\u91cf\u5b8c\u6210') }
    $failRegex = (U '\u5f15\u64ce\u4e0d\u53ef\u7528')
    $processingRegex = (U '\u5904\u7406\u4e2d|\u6b63\u5728\u5904\u7406|\u7b2c\s*\d+\s*/\s*\d+\s*\u5f20')
    $sawProcessing = $false
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) { throw "Lumen exited while processing (code $($process.ExitCode))." }
        $snap = Get-UiSnapshot -Hwnd $result.hwnd
        if ($null -ne $snap) {
            if (Test-SnapshotRegex $snap $failRegex) { return 'failed' }
            foreach ($text in (Get-SnapshotTexts $snap)) {
                if ($text -match $processingRegex) { $sawProcessing = $true }
                if ($text -match $doneRegex) { return 'completed' }
            }
        }
        Start-Sleep -Seconds 2
    }
    $result.notes += ("completion timeout after {0}s (sawProcessing={1})" -f $TimeoutSeconds, $sawProcessing)
    return 'timeout'
}

try {
    foreach ($key in @('LUMEN_OPEN', 'LUMEN_OPEN_MANY', 'LUMEN_THEME', 'LUMEN_AUTOSTART', 'LUMEN_SAVE_TO',
            'LUMEN_SAVE_DIR', 'LUMEN_BACKEND', 'LUMEN_PATTERN', 'LUMEN_SCALE', 'LUMEN_MODELS_ROOT', 'LUMEN_CONFLICT', 'LUMEN_FORCE_NO_DML')) {
        Remove-Item "Env:$key" -ErrorAction SilentlyContinue
    }
    foreach ($key in $envMap.Keys) { Set-Item "Env:$key" ([string]$envMap[$key]) }

    $other = @(Get-Process -Name 'Lumen' -ErrorAction SilentlyContinue)
    if ($other.Count -gt 0) {
        $result.notes += "Other Lumen instance(s) already running: $(($other | ForEach-Object { $_.Id }) -join ', ')."
    }

    $process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $result.pid = $process.Id
    Step ("launched PID {0} scenario={1}" -f $process.Id, $Scenario)
    $window = Wait-LumenWindow -Process $process -TimeoutSeconds $WindowTimeoutSeconds
    # Installer layout: root Lumen.exe is a launcher that exits after spawning
    # runtime\Lumen.exe. From here on, track the process that owns the window.
    $windowProcess = Get-Process -Id $window.processId -ErrorAction SilentlyContinue
    if ($windowProcess) {
        $process = $windowProcess
        $result.pid = $process.Id
    }
    $result.hwnd = [int64]$window.hwnd
    $result.windowTitle = [string]$window.title
    Step ("window: hwnd=0x{0:X} title='{1}'" -f [int64]$window.hwnd, $window.title)
    $settleSeconds = 3
    if ($Scenario -eq 'compat-missing-input') { $settleSeconds = 1 }   # InfoBar auto-closes after 3s
    Start-Sleep -Seconds $settleSeconds

    # ---- window title assertion ----
    $result.assertions += [ordered]@{
        key = 'WindowTitle'; automationId = '(window)'; required = $true
        found = ($window.title -eq 'Lumen'); via = 'window'; type = 'Window'
        detail = [string]$window.title; offscreen = $false
    }

    # ---- control assertions ----
    $snapshot = Get-UiSnapshot -Hwnd $result.hwnd
    $checks = @()
    foreach ($item in $contractV1) { $checks += $item }
    if ($scenarioInfo.Queue -or $Scenario -in @('cpu-single', 'gpu-single', 'system-info', 'compat-empty-models', 'compat-missing-input', 'compat-no-dml')) {
        foreach ($item in $contractV2) { $checks += $item }
    }
    $loadsImage = ($Scenario -in @('batch-load', 'batch-run', 'batch-cpu', 'batch-skip', 'compat-bad-inputs', 'cpu-single', 'gpu-single')) -or
        ($Scenario -eq 'single' -and -not [string]::IsNullOrWhiteSpace($OpenImage))
    foreach ($item in $checks) {
        $required = $false
        if ($item.Required) { $required = $true }
        if ($item.RequiredFor) {
            $required = ($Scenario -in $item.RequiredFor)
        }
        if ($item.LoadedOnly -and -not $loadsImage) {
            $required = $false
        }
        $match = Find-SnapshotElement -Elements $snapshot -Ids $item.Ids -Names $item.Names -NameRegex $item.NameRegex
        $element = $match.Element
        $firstId = 'n/a'
        if ($item.Ids) { $firstId = @($item.Ids)[0] }
        $record = [ordered]@{
            key = $item.Key
            automationId = $firstId
            required = $required
            found = $match.Found
            via = $match.Via
            type = ''
            detail = ''
            offscreen = $null
        }
        if ($element) {
            $record.type = [string]$element.type
            $record.detail = [string]$element.name
            $record.offscreen = [bool]$element.isOffscreen
        }
        $result.assertions += $record
        if ($match.Via -eq 'name' -or $match.Via -eq 'nameRegex') {
            $result.fallbackControls += $item.Key
            foreach ($id in $item.Ids) { if ($id) { $result.missingAutomationIds += $id } }
        }
        elseif (-not $match.Found -and $required) {
            foreach ($id in $item.Ids) { if ($id) { $result.missingAutomationIds += $id } }
        }
        if ($match.Found) { Step ("control {0,-24} found via {1}" -f $item.Key, $match.Via) }
        elseif ($required) { Step ("control {0,-24} MISSING (required)" -f $item.Key) }
    }
    $result.missingAutomationIds = @($result.missingAutomationIds | Select-Object -Unique)

    # ---- initial screenshot ----
    if (-not $NoScreenshot) {
        $shot1 = Join-Path $ArtifactDirectory '01-initial.png'
        $capture = Save-WindowScreenshot -Hwnd $result.hwnd -Path $shot1
        $result.screenshots += $capture.Path
        Step ("screenshot 01-initial.png {0}x{1} colors={2} blank={3}" -f $capture.Width, $capture.Height, $capture.DistinctColors, $capture.LooksBlank)
        if ($capture.LooksBlank) { $result.notes += 'Initial screenshot looks blank (single color).' }
    }
    $result.phases['initial'] = 'ok'

    # ---- scenario actions ----
    if ($action -eq 'start-single' -or $action -eq 'start-batch') {
        Start-Sleep -Seconds 2
        $clicked = Invoke-Control -Ids @('BtnStartAll', 'BtnStart') -Names @(
            (U '\u5f00\u59cb\u5904\u7406\u5168\u90e8'), (U '\u5f00\u59cb\u8d85\u5206'), (U '\u5f00\u59cb\u8d85\u5206\u6216\u53d6\u6d88\u5904\u7406'))
        if (-not $clicked) {
            $result.notes += 'Could not invoke the start action.'
            $result.phases['action'] = 'invoke-failed'
        }
        else {
            Step ("invoke start via '{0}'" -f $clicked)
            Start-Sleep -Seconds 2
            if (-not $NoScreenshot) {
                $shot2 = Join-Path $ArtifactDirectory '02-processing.png'
                $capture2 = Save-WindowScreenshot -Hwnd $result.hwnd -Path $shot2
                $result.screenshots += $capture2.Path
                Step ("screenshot 02-processing.png colors={0}" -f $capture2.DistinctColors)
            }
        }
    }

    if ($action -eq 'start-batch' -or $action -eq 'wait-batch') {
        $phase = Wait-Completion -Kind 'batch' -TimeoutSeconds $UpscaleTimeoutSeconds
        $result.phases['batch'] = $phase
        if ($phase -eq 'completed') {
            Start-Sleep -Seconds 2
            $snapDone = Get-UiSnapshot -Hwnd $result.hwnd
            $summaryText = ''
            foreach ($text in (Get-SnapshotTexts $snapDone)) {
                if ($text -match (U '\u6210\u529f\s*\d+\s*/\s*\u5931\u8d25\s*\d+')) { $summaryText = $text; break }
            }
            if ($summaryText) { $result.phases['summary'] = $summaryText }
            else { $result.notes += 'Batch completed but summary "success N / failed M" was not found.' }
            if (-not $NoScreenshot) {
                $shot3 = Join-Path $ArtifactDirectory '03-completed.png'
                $capture3 = Save-WindowScreenshot -Hwnd $result.hwnd -Path $shot3
                $result.screenshots += $capture3.Path
                Step ("screenshot 03-completed.png colors={0}" -f $capture3.DistinctColors)
            }
            if ($SaveDir -and $Scenario -ne 'batch-skip') {
                $result.outputVerification = Test-BatchOutputs -Directory $SaveDir -InputPaths @($expectedOutputs) -Scale $scaleValue
                Step ("outputs: {0} files (expected {1}) dimsOk={2}" -f $result.outputVerification.actualCount, $result.outputVerification.expectedCount, $result.outputVerification.dimensionsOk)
            }
            if ($Scenario -eq 'compat-bad-inputs') {
                $failedFound = Test-SnapshotRegex $snapDone (U '\u5931\u8d25')
                $result.assertions += [ordered]@{
                    key = 'FailedItemIsolation'; automationId = '(snapshot)'; required = $true
                    found = $failedFound; via = 'nameRegex'; type = 'Text'; detail = 'failed item state present'; offscreen = $false
                }
            }
            if ($Scenario -eq 'batch-skip') {
                # Skipped item: queue item/summary mentions skipped, and the pre-created
                # output file must be untouched (Lead decision C-1: OutputPath reported,
                # file not written/overwritten).
                $skippedFound = Test-SnapshotRegex $snapDone (U '\u8df3\u8fc7|Skipped')
                $skipSummary = Test-SnapshotRegex $snapDone (U '\u8df3\u8fc7\s*\d+')
                $fileOk = $false
                $fileDetail = 'sentinel missing'
                if ($skipSentinelPath -and (Test-Path -LiteralPath $skipSentinelPath)) {
                    $hashAfter = (Get-FileHash -LiteralPath $skipSentinelPath -Algorithm SHA256).Hash
                    $imageCount = @(Get-ChildItem -LiteralPath $SaveDir -File -ErrorAction SilentlyContinue |
                        Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|webp|bmp)$' }).Count
                    $fileOk = ($hashAfter -eq $skipSentinelHash) -and ($imageCount -eq 1)
                    $fileDetail = ("hashSame={0} imageCount={1} file={2}" -f ($hashAfter -eq $skipSentinelHash), $imageCount, $skipSentinelPath)
                }
                $result.assertions += [ordered]@{
                    key = 'SkippedItemState'; automationId = '(snapshot)'; required = $true
                    found = $skippedFound; via = 'nameRegex'; type = 'Text'; detail = 'skipped item state present'; offscreen = $false
                }
                $result.assertions += [ordered]@{
                    key = 'SkipSummaryCount'; automationId = '(snapshot)'; required = $true
                    found = $skipSummary; via = 'nameRegex'; type = 'Text'; detail = 'summary contains skipped N'; offscreen = $false
                }
                $result.assertions += [ordered]@{
                    key = 'SkipOutputUntouched'; automationId = '(filesystem)'; required = $true
                    found = $fileOk; via = 'filesystem'; type = 'File'; detail = $fileDetail; offscreen = $false
                }
                $result.phases['skip-policy'] = if ($fileOk) { 'ok' } else { 'mismatch' }
            }
        }
        elseif ($phase -ne 'completed') {
            $result.notes += "Batch did not complete (phase=$phase)."
        }
    }
    elseif ($action -eq 'start-single' -or $action -eq 'wait-single') {
        $phase = Wait-Completion -Kind 'single' -TimeoutSeconds $UpscaleTimeoutSeconds
        $result.phases['single'] = $phase
        if ($phase -eq 'completed') {
            Start-Sleep -Seconds 1
            if (-not $NoScreenshot) {
                $shot2 = Join-Path $ArtifactDirectory '02-single-completed.png'
                $capture2 = Save-WindowScreenshot -Hwnd $result.hwnd -Path $shot2
                $result.screenshots += $capture2.Path
            }
            if ($envMap['LUMEN_BACKEND'] -eq 'cpu') {
                $snapDone = Get-UiSnapshot -Hwnd $result.hwnd
                $cpuFound = Test-SnapshotRegex $snapDone 'CPU'
                $result.assertions += [ordered]@{
                    key = 'BackendCpuLabel'; automationId = '(snapshot)'; required = $true
                    found = $cpuFound; via = 'nameRegex'; type = 'Text'; detail = 'CPU label'; offscreen = $false
                }
            }
        }
        else {
            $result.notes += "Single-image run did not complete (phase=$phase)."
        }
    }
    elseif ($action -eq 'system-info') {
        Start-Sleep -Seconds 1
        $clicked = Invoke-Control -Ids @('BtnSystemInfo') -Names @()
        if (-not $clicked) {
            $result.notes += 'Could not invoke BtnSystemInfo.'
            $result.phases['system-info'] = 'invoke-failed'
        }
        else {
            Start-Sleep -Seconds 2
            $snapDialog = Get-UiSnapshot -Hwnd $result.hwnd
            if (-not $NoScreenshot) {
                $shot2 = Join-Path $ArtifactDirectory '02-system-info.png'
                $capture2 = Save-WindowScreenshot -Hwnd $result.hwnd -Path $shot2
                $result.screenshots += $capture2.Path
            }
            foreach ($id in @('DlgSystemInfo', 'TxtSysOs', 'TxtSysCpu', 'TxtSysGpu', 'TxtSysDirectMl', 'TxtSysModels', 'TxtSysLogPath')) {
                $found = @($snapDialog | Where-Object { $_.automationId -eq $id }).Count -gt 0
                $via = 'missing'
                if ($found) { $via = 'automationId' }
                $result.assertions += [ordered]@{
                    key = $id; automationId = $id; required = $true
                    found = $found; via = $via; type = 'Dialog'; detail = ''; offscreen = $false
                }
            }
            [void](Invoke-Ui @('send-keys', 'esc', '-w', "$($result.hwnd)"))
            $result.phases['system-info'] = 'opened'
        }
    }
    elseif ($Scenario -eq 'batch-load') {
        Start-Sleep -Seconds 2
        $snapQueue = Get-UiSnapshot -Hwnd $result.hwnd
        $summaryFound = Test-SnapshotRegex $snapQueue (U '\d+\s*\u5f20')
        $result.assertions += [ordered]@{
            key = 'QueueSummaryCount'; automationId = 'TxtQueueSummary'; required = $true
            found = $summaryFound; via = 'nameRegex'; type = 'Text'; detail = 'queue count text'; offscreen = $false
        }
        $queueNamesOk = 0
        foreach ($input in $openManyList) {
            $name = [IO.Path]::GetFileName($input)
            if (Test-SnapshotRegex $snapQueue ([regex]::Escape($name))) { $queueNamesOk++ }
        }
        $expectedListed = [Math]::Min(2, $openManyList.Count)
        $result.assertions += [ordered]@{
            key = 'QueueItemsListed'; automationId = '(snapshot)'; required = $true
            found = ($queueNamesOk -ge $expectedListed); via = 'nameRegex'; type = 'ListItems'
            detail = ("{0}/{1} input file names found in UIA tree" -f $queueNamesOk, $openManyList.Count); offscreen = $false
        }
    }
    elseif ($Scenario -eq 'compat-empty-models') {
        $snapCompat = Get-UiSnapshot -Hwnd $result.hwnd
        $modelHint = Test-SnapshotRegex $snapCompat (U '\u6a21\u578b')
        $result.assertions += [ordered]@{
            key = 'EmptyModelsHint'; automationId = '(snapshot)'; required = $true
            found = $modelHint; via = 'nameRegex'; type = 'Text'; detail = 'model-related hint present'; offscreen = $false
        }
        $startEl = @($snapCompat | Where-Object { $_.automationId -eq 'BtnStart' -or $_.automationId -eq 'BtnStartAll' }) | Select-Object -First 1
        $startDisabled = $false
        if ($startEl) { $startDisabled = (-not [bool]$startEl.isEnabled) }
        $result.assertions += [ordered]@{
            key = 'StartDisabledWithoutModels'; automationId = '(snapshot)'; required = $false
            found = $startDisabled; via = 'property'; type = 'Button'; detail = 'disabled when no model'; offscreen = $false
        }
        $result.phases['compat-empty-models'] = 'checked'
    }
    elseif ($Scenario -eq 'compat-no-dml') {
        Start-Sleep -Seconds 2
        $snapDml = Get-UiSnapshot -Hwnd $result.hwnd
        $cpuHint = Test-SnapshotRegex $snapDml (U '\u672a\u68c0\u6d4b\u5230.{0,20}DirectML|DirectML.{0,20}(\u4e0d\u53ef\u7528|\u672a\u68c0\u6d4b\u5230)|\u5df2\u5207\u6362.{0,20}CPU|CPU \u517c\u5bb9')
        $result.assertions += [ordered]@{
            key = 'NoDmlCpuFallbackHint'; automationId = '(snapshot)'; required = $true
            found = $cpuHint; via = 'nameRegex'; type = 'Text'; detail = 'auto -> CPU compatibility hint'; offscreen = $false
        }
        $backendEl = @($snapDml | Where-Object { $_.automationId -eq 'TxtBackend' }) | Select-Object -First 1
        $backendCpu = $false
        $backendText = '(missing)'
        if ($backendEl) {
            $backendText = [string]$backendEl.name
            if ($backendText -match 'CPU') { $backendCpu = $true }
        }
        $result.assertions += [ordered]@{
            key = 'BackendShowsCpu'; automationId = 'TxtBackend'; required = $true
            found = $backendCpu; via = 'automationId'; type = 'Text'; detail = ("TxtBackend=" + $backendText); offscreen = $false
        }
        $result.phases['compat-no-dml'] = 'checked'
        if (-not $NoScreenshot) {
            $shot2 = Join-Path $ArtifactDirectory '02-no-dml.png'
            $capture2 = Save-WindowScreenshot -Hwnd $result.hwnd -Path $shot2
            $result.screenshots += $capture2.Path
        }
    }
    elseif ($Scenario -eq 'compat-missing-input') {
        # Reuse the snapshot taken right after the window appeared (the warning
        # InfoBar auto-closes after 3 seconds, so a second inspection may miss it).
        $snapCompat = $snapshot
        $errorFound = Test-SnapshotRegex $snapCompat (U '\u65e0\u6cd5\u6253\u5f00|\u4e0d\u5b58\u5728|\u6253\u5f00\u5931\u8d25|\u8def\u5f84\u4e0d\u53ef\u7528')
        $result.assertions += [ordered]@{
            key = 'MissingInputError'; automationId = '(snapshot)'; required = $true
            found = $errorFound; via = 'nameRegex'; type = 'Text'; detail = 'Chinese missing-file error'; offscreen = $false
        }
        $statusTextFound = Test-SnapshotRegex $snapCompat (U '\u8def\u5f84.{0,12}(\u4e0d\u5b58\u5728|\u4e0d\u53ef\u7528)|\u4e0d\u5b58\u5728\u6216\u4e0d\u53ef\u7528')
        $result.assertions += [ordered]@{
            key = 'MissingInputStatusText'; automationId = '(snapshot)'; required = $true
            found = $statusTextFound; via = 'nameRegex'; type = 'Text'; detail = 'status text says path missing/unusable'; offscreen = $false
        }
        $result.phases['compat-missing-input'] = 'checked'
        if (-not $NoScreenshot) {
            $shot2 = Join-Path $ArtifactDirectory '02-error.png'
            $capture2 = Save-WindowScreenshot -Hwnd $result.hwnd -Path $shot2
            $result.screenshots += $capture2.Path
        }
    }

    # ---- close (only our instance) ----
    if (-not $KeepAlive) {
        Step "closing PID $($process.Id) via WM_CLOSE"
        [void][LumenSmokeWin32]::PostMessage([IntPtr]$result.hwnd, [LumenSmokeWin32]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $process.WaitForExit(10000)) {
            $result.notes += 'Process did not exit within 10s after WM_CLOSE; force-killed.'
            try { $process.Kill() } catch { }
            [void]$process.WaitForExit(5000)
        }
        $result.phases['close'] = if ($process.HasExited) { 'exited' } else { 'force-killed' }
        Step "process exited=$($process.HasExited) code=$(if ($process.HasExited) { $process.ExitCode } else { 'n/a' })"
    }
    else {
        $result.phases['close'] = 'kept-alive'
        $result.notes += "KeepAlive: PID $($process.Id) left running."
    }

    $requiredFailed = @($result.assertions | Where-Object { $_.required -and -not $_.found })
    $phaseFailure = $false
    foreach ($value in $result.phases.Values) {
        if ($value -in @('timeout', 'invoke-failed', 'failed')) { $phaseFailure = $true }
    }
    $result.ok = ($requiredFailed.Count -eq 0) -and (-not $phaseFailure)
}
catch {
    $fatalError = $true
    $result.notes += ("ERROR: {0}" -f $_.Exception.Message)
}
finally {
    foreach ($key in $previousEnv.Keys) {
        if ($null -eq $previousEnv[$key]) { Remove-Item "Env:$key" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$key" $previousEnv[$key] }
    }
    if ($null -ne $process -and -not $process.HasExited -and -not $KeepAlive) {
        try { $process.Kill(); [void]$process.WaitForExit(5000) } catch { }
        $result.notes += 'Cleanup: killed the launched Lumen process after an error.'
    }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
$result.elapsedSeconds = [Math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
$resultPath = Join-Path $ArtifactDirectory 'result.json'
Set-Content -LiteralPath $resultPath -Value ($result | ConvertTo-Json -Depth 12) -Encoding UTF8

$requiredFailedCount = @($result.assertions | Where-Object { $_.required -and -not $_.found }).Count
Write-Host ''
Write-Host '==== ui-smoke v2 summary ===='
Write-Host ("scenario: {0} ({1})" -f $Scenario, $scenarioInfo.Description)
Write-Host ("ok={0}  pid={1}  hwnd={2}  elapsed={3}s" -f $result.ok, $result.pid, $result.hwnd, $result.elapsedSeconds)
Write-Host ("assertions: {0} total, {1} required failed" -f $result.assertions.Count, $requiredFailedCount)
$phaseText = @()
foreach ($key in $result.phases.Keys) { $phaseText += ("{0}={1}" -f $key, $result.phases[$key]) }
Write-Host ("phases: {0}" -f ($phaseText -join ' '))
if ($result.outputVerification) {
    Write-Host ("outputs: {0} files (expected {1}), dimsOk={2}" -f `
        $result.outputVerification.actualCount, $result.outputVerification.expectedCount, $result.outputVerification.dimensionsOk)
}
Write-Host ("screenshots: {0}" -f (($result.screenshots) -join ', '))
Write-Host ("missing AutomationIds: {0}" -f (($result.missingAutomationIds) -join ', '))
Write-Host ("result json: {0}" -f $resultPath)

if ($result.ok -and -not $fatalError) { exit 0 }
exit 1
