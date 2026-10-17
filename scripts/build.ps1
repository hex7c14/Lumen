# Lumen 构建脚本
#
# 本机 .NET 10 SDK 安装在用户目录（不在 Program Files\dotnet），脚本会自动定位：
#   1. PATH 中的 dotnet
#   2. %USERPROFILE%\dotnet10\dotnet.exe
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts/build.ps1                 # Debug 构建全部
#   powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Release
#   powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -Publish       # 发布可运行产物到 artifacts\Lumen
#   powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -Publish -Zip  # 同时打包为 zip
#
# 发布布局（根目录干净，运行时收进隐藏的 runtime\）：
#   artifacts\Lumen\
#     Lumen.exe          <- 启动器（双击入口，带应用图标）
#     models\            <- 模型（可见，用户可自行增删）
#     output\            <- 默认输出目录（可见，可自行使用）
#     README.md
#     runtime\           <- 真正的 WinUI 程序与全部运行时依赖（隐藏）
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Publish,
    [switch]$Zip,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        $candidate = Join-Path $env:USERPROFILE "dotnet10\dotnet.exe"
        if (Test-Path $candidate) {
            $dotnet = $candidate
            $env:Path = (Split-Path $candidate) + ";" + $env:Path
        } else {
            throw "找不到 dotnet。请安装 .NET 10 SDK 或在 PATH 中提供 dotnet。"
        }
    }

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"

    Write-Host "==> dotnet: $dotnet"
    Write-Host "==> 构建 Lumen.slnx ($Configuration)"

    $buildArgs = @("build", "Lumen.slnx", "-c", $Configuration, "--nologo")
    if ($SkipRestore) { $buildArgs += "--no-restore" }
    & $dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）。" }

    if ($Publish) {
        $outDir = Join-Path $root "artifacts\Lumen"
        $runtimeDir = Join-Path $outDir "runtime"
        Write-Host "==> 发布到 $outDir（根目录：Lumen.exe / models / output / README；运行时目录 runtime 隐藏）"
        if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
        New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

        # 1) 真正的 WinUI 程序与全部运行时依赖 -> runtime\
        & $dotnet publish "src\Lumen.App\Lumen.App.csproj" `
            -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 `
            -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false `
            -o $runtimeDir --nologo
        if ($LASTEXITCODE -ne 0) { throw "发布失败（退出码 $LASTEXITCODE）。" }

        # 2) WinUI 的 XAML 资源（Lumen.pri 与 *.xbf）只标记了 CopyToOutputDirectory，
        #    dotnet publish 不会自动复制；缺失会在启动时抛 XamlParseException。
        #    注意 *.xbf 分布在 Views/、Controls/ 等子目录，必须递归复制并保留相对路径。
        $targetDir = (& $dotnet msbuild "src\Lumen.App\Lumen.App.csproj" `
            -p:Configuration=$Configuration -p:Platform=x64 -getProperty:TargetDir).Trim()
        if (-not (Test-Path $targetDir)) { throw "找不到构建输出目录：$targetDir" }

        $priSource = Join-Path $targetDir "Lumen.pri"
        if (Test-Path $priSource) { Copy-Item $priSource (Join-Path $runtimeDir "Lumen.pri") -Force }
        $xbfCount = 0
        Get-ChildItem $targetDir -Recurse -Filter "*.xbf" -File | ForEach-Object {
            $relative = $_.FullName.Substring($targetDir.Length).TrimStart('\')
            $destination = Join-Path $runtimeDir $relative
            $destinationDirectory = Split-Path $destination -Parent
            if (-not (Test-Path $destinationDirectory)) {
                New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            }
            Copy-Item $_.FullName $destination -Force
            $xbfCount++
        }
        if (-not (Test-Path (Join-Path $runtimeDir "Lumen.pri")) -or $xbfCount -eq 0) {
            throw "发布产物缺少 XAML 资源（Lumen.pri / *.xbf），应用无法启动。"
        }
        Write-Host "==> XAML 资源已补齐到 runtime\：Lumen.pri + $xbfCount 个 .xbf（含子目录）"

        # 3) 根目录启动器：极小单文件 exe，负责拉起 runtime\Lumen.exe
        & $dotnet publish "src\Lumen.Launcher\Lumen.Launcher.csproj" `
            -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 `
            -p:PublishSingleFile=true -p:PublishTrimmed=true `
            -o $outDir --nologo
        if ($LASTEXITCODE -ne 0) { throw "启动器发布失败（退出码 $LASTEXITCODE）。" }
        if (-not (Test-Path (Join-Path $outDir "Lumen.exe"))) {
            throw "启动器 Lumen.exe 未生成。"
        }
        Write-Host "==> 根目录启动器已生成：$outDir\Lumen.exe"

        # 4) 根目录可见资源：models、output、README
        $modelsSource = Join-Path $root "models"
        if (Test-Path $modelsSource) {
            Copy-Item $modelsSource (Join-Path $outDir "models") -Recurse -Force
        }
        $outputDir = Join-Path $outDir "output"
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

        foreach ($doc in @("README.md")) {
            $source = Join-Path $root $doc
            if (Test-Path $source) { Copy-Item $source (Join-Path $outDir $doc) -Force }
        }

        # 5) 清理发布产物中运行时不需要的调试文件（*.pdb、*.lib、DirectML 调试层）。
        #    这些只用于调试符号/链接，不影响运行；Skia/DirectML 的 pdb 加起来约 100 MB。
        $debugFiles = @(Get-ChildItem $outDir -Recurse -File -Force | Where-Object {
            $_.Extension -in @('.pdb', '.lib') -or $_.Name -eq 'DirectML.Debug.dll'
        })
        if ($debugFiles.Count -gt 0) {
            $removedBytes = ($debugFiles | Measure-Object Length -Sum).Sum
            $debugFiles | ForEach-Object { Remove-Item $_.FullName -Force }
            Write-Host ("==> 已清理调试文件：{0} 个，{1:N1} MB" -f $debugFiles.Count, ($removedBytes / 1MB))
        }

        # 6) 隐藏 runtime\：根目录只留 Lumen.exe / models / output / README
        $runtimeItem = Get-Item -Force $runtimeDir
        $runtimeItem.Attributes = $runtimeItem.Attributes -bor [System.IO.FileAttributes]::Hidden

        # 7) 自检：启动根目录的 Lumen.exe -> 启动器拉起 runtime\Lumen.exe，等待主窗口出现。
        $publishedExe = Join-Path $outDir "Lumen.exe"
        Get-Process -Name Lumen -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Process -FilePath $publishedExe -WorkingDirectory $outDir | Out-Null
        $deadline = (Get-Date).AddSeconds(20)
        $windowReady = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $running = @(Get-Process -Name Lumen -ErrorAction SilentlyContinue |
                Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
            if ($running.Count -gt 0) { $windowReady = $true; break }
        }
        Get-Process -Name Lumen -ErrorAction SilentlyContinue | Stop-Process -Force
        if (-not $windowReady) {
            throw "发布产物启动自检失败：未能通过启动器打开主窗口。请查看 $env:LOCALAPPDATA\Lumen\logs\lumen.log。"
        }
        Write-Host "==> 启动自检通过（启动器 + runtime 主窗口已出现）"

        # 8) 可选：打包 zip（保留隐藏 runtime；整个 artifacts\Lumen 目录）
        if ($Zip) {
            $zipPath = Join-Path $root "artifacts\Lumen-win-x64.zip"
            if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $outDir, $zipPath,
                [System.IO.Compression.CompressionLevel]::Optimal,
                $false)
            $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
            Write-Host "==> 已打包：$zipPath（$zipSize MB）"
        }

        $sizeMb = [math]::Round(((Get-ChildItem $outDir -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
        Write-Host "==> 完成：$outDir（$sizeMb MB）"
        Write-Host "    双击运行：$outDir\Lumen.exe"
    }
} finally {
    Pop-Location
}
