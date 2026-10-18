# 构建并启动 Lumen 桌面应用（开发用）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts/run.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/run.ps1 -Configuration Release
#   powershell -ExecutionPolicy Bypass -File scripts/run.ps1 -Architecture arm64
#
# -Architecture auto|x64|arm64（默认 auto = 当前 Windows 机器架构）
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("auto", "x64", "arm64")]
    [string]$Architecture = "auto",
    [switch]$SkipBuild,
    [string]$OpenImage = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    function Resolve-TargetArchitecture([string]$Value) {
        if ($Value -ne "auto") { return $Value }
        if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { return "arm64" }
        return "x64"
    }

    $targetArchitecture = Resolve-TargetArchitecture $Architecture
    $platform = if ($targetArchitecture -eq "arm64") { "ARM64" } else { "x64" }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        $candidate = Join-Path $env:USERPROFILE "dotnet10\dotnet.exe"
        if (-not (Test-Path $candidate)) { throw "找不到 dotnet。" }
        $dotnet = $candidate
        $env:Path = (Split-Path $candidate) + ";" + $env:Path
    }

    if (-not $SkipBuild) {
        & $dotnet build "src\Lumen.App\Lumen.App.csproj" -c $Configuration --nologo -p:Platform=$platform
        if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）。" }
    }

    # 只选目标架构目录（bin\<Platform>\<Configuration>\...\win-<arch>\），避免误启动另一架构的旧产物。
    $binRoot = Join-Path $root "src\Lumen.App\bin\$platform"
    $exe = Get-ChildItem $binRoot -Recurse -Filter "Lumen.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match [regex]::Escape("win-$targetArchitecture") } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $exe) { throw "找不到 $targetArchitecture 架构的 Lumen.exe，请先构建（-p:Platform=$platform）。" }

    Write-Host "==> 架构：$targetArchitecture"

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
