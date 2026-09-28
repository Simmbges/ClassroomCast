# 教室屏幕共享 - 打包发布脚本
# 用法：powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
# 需要先安装 .NET 8 SDK（见 README.md）。
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dotnet = "dotnet"
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }

Set-Location $root

Write-Host "== 1/2 发布免安装版（self-contained，目标电脑无需安装任何运行时）==" -ForegroundColor Cyan
& $dotnet publish src/ScreenShare -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "publish/教室屏幕共享-免安装版"
if ($LASTEXITCODE -ne 0) { throw "免安装版发布失败" }

Write-Host "== 2/2 发布轻量版（framework-dependent，目标电脑需安装 .NET 8 桌面运行时）==" -ForegroundColor Cyan
& $dotnet publish src/ScreenShare -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "publish/教室屏幕共享-需运行时版"
if ($LASTEXITCODE -ne 0) { throw "轻量版发布失败" }

Write-Host ""
Write-Host "完成。产物：" -ForegroundColor Green
Get-ChildItem "publish" -Directory | ForEach-Object {
    $exe = Join-Path $_.FullName "ClassroomScreenShare.exe"
    if (Test-Path $exe) {
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host "  $($_.Name)\ClassroomScreenShare.exe  ${size} MB"
    }
}
