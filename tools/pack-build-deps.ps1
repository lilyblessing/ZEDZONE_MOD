# pack-build-deps.ps1
# 打包编译所需依赖（游戏 interop 程序集 + BepInEx core + unity-libs）为 build-deps.zip。
#
# 用法：
#   首次设置 CI 时需要运行一次，把生成的 build-deps.zip 上传到 GitHub release（tag: build-deps）。
#   之后 CI（.github/workflows/release.yml）会自动下载该依赖包完成编译。
#
# 可选参数：-GameDir <游戏目录> -Out <输出zip路径>

param(
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\ZED ZONE",
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"

if ($Out -eq "") {
    $Out = Join-Path $PSScriptRoot "..\build-deps.zip"
}

$paths = @(
    (Join-Path $GameDir "BepInEx\core"),
    (Join-Path $GameDir "BepInEx\interop"),
    (Join-Path $GameDir "BepInEx\unity-libs")
)

foreach ($p in $paths) {
    if (-not (Test-Path $p)) {
        Write-Error "路径不存在: $p"
        exit 1
    }
}

Write-Host "打包构建依赖:"
foreach ($p in $paths) { Write-Host "  - $p" }
Write-Host "输出: $Out"

Compress-Archive -Path $paths -DestinationPath $Out -Force
$size = (Get-Item $Out).Length / 1MB
Write-Host "完成: build-deps.zip ($([math]::Round($size,1)) MB)"
Write-Host ""
Write-Host "下一步: 在 GitHub 创建 tag 'build-deps' 和 release，上传 build-deps.zip 作为 asset。"
Write-Host "之后每次推送 v* tag 都会自动编译并发布 release。"
