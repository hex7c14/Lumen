# 构建并启动 Lumen 桌面应用（开发用）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts/run.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/run.ps1 -Configuration Release
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [string]$OpenImage = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        $candidate = Join-Path $env:USERPROFILE "dotnet10\dotnet.exe"
        if (-not (Test-Path $candidate)) { throw "找不到 dotnet。" }
        $dotnet = $candidate
        $env:Path = (Split-Path $candidate) + ";" + $env:Path
    }

    if (-not $SkipBuild) {
        & $dotnet build "src\Lumen.App\Lumen.App.csproj" -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）。" }
    }

    $exe = Get-ChildItem "src\Lumen.App\bin" -Recurse -Filter "Lumen.exe" |
        Where-Object { $_.FullName -match [regex]::Escape($Configuration) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $exe) { throw "找不到 Lumen.exe，请先构建。" }

    if ($OpenImage) {
        $resolved = (Resolve-Path $OpenImage).Path
        $env:LUMEN_OPEN = $resolved
        Write-Host "==> LUMEN_OPEN=$resolved"
    }

    Write-Host "==> 启动 $($exe.FullName)"
    Start-Process -FilePath $exe.FullName -WorkingDirectory (Split-Path $exe.FullName)
} finally {
    Pop-Location
}
