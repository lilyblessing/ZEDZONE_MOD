# make-release.ps1
# 本地编译并打包发布产物（手动发布到 GitHub Release 使用）。
#
# 用法：
#   .\tools\make-release.ps1                  # 打包全部 mod（notetag + bigfridge + portablefridge）
#   .\tools\make-release.ps1 -Mod notetag     # 只打包 NoteTag
#   .\tools\make-release.ps1 -Mod bigfridge   # 只打包 BigFridge
#   .\tools\make-release.ps1 -Mod portablefridge  # 只打包 PortableFridge
#   .\tools\make-release.ps1 -GameDir "X:\..."  # 指定游戏目录（编译依赖 interop）
#
# 产物：dist\<DllName>-<版本>.zip（dll + 资源文件，不含 README），版本自动从各 Plugin.cs 读取

param(
    [ValidateSet("notetag", "bigfridge", "portablefridge", "all")]
    [string]$Mod = "all",
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\ZED ZONE"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Build-Mod {
    param([string]$ProjDir, [string]$DllName, [string[]]$Resources)

    Write-Host "=== 编译 $ProjDir ==="
    Push-Location (Join-Path $root $ProjDir)
    try {
        $env:GAME_DIR = $GameDir
        dotnet build -c Release
        if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }
        Remove-Item Env:GAME_DIR
    } finally {
        Pop-Location
    }

    # 版本号从 Plugin.cs 的 BepInPlugin 特性读取
    $match = Select-String -Path (Join-Path $root "$ProjDir\Plugin.cs") -Pattern 'BepInPlugin\("[^"]+", "[^"]+", "([^"]+)"\)'
    $ver = if ($match) { "v" + $match.Matches[0].Groups[1].Value } else { "v0.0.0" }

    Write-Host "=== 打包 $DllName $ver ==="
    $distDir = Join-Path $root "dist"
    $stage = Join-Path $distDir "stage"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    Copy-Item (Join-Path $root "$ProjDir\bin\Release\net6.0\$DllName.dll") $stage -Force
    foreach ($r in $Resources) {
        Copy-Item (Join-Path $root "$ProjDir\$r") $stage -Force
    }

    $zip = Join-Path $distDir "$DllName-$ver.zip"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force
    Write-Host "发布包: $zip ($([math]::Round((Get-Item $zip).Length/1KB,1)) KB)"
    return $ver
}

Write-Host "=== 开始打包（GAME_DIR=$GameDir）==="
if ($Mod -in @("notetag", "all")) {
    Build-Mod -ProjDir "NoteTagPlugin" -DllName "NoteTagPlugin" -Resources @("Name_Tag.png")
}
if ($Mod -in @("bigfridge", "all")) {
    Build-Mod -ProjDir "FridgeModPlugin" -DllName "FridgeModPlugin" -Resources @()
}
if ($Mod -in @("portablefridge", "all")) {
    Build-Mod -ProjDir "PortableFridgePlugin" -DllName "PortableFridgePlugin" -Resources @("Portable_Fridge.png")
}
Write-Host ""
Write-Host "=== 全部完成 ==="
Write-Host "下一步：在 GitHub 创建 release（tag 建议 vX.Y.Z），上传 dist 下的 zip。"
