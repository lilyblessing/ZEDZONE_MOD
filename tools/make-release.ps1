# make-release.ps1
# 本地编译并打包发布产物（手动发布到 GitHub Release 使用）。
#
# 用法：
#   .\tools\make-release.ps1                # 编译 + 打包（版本号自动从 Plugin.cs 读取）
#   .\tools\make-release.ps1 -Version v9.9  # 指定版本号（覆盖）
#
# 产物：dist\NoteTagPlugin-<version>.zip（NoteTagPlugin.dll + Name_Tag.png + README.md）

param(
    [string]$Version = "",
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\ZED ZONE"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pluginDir = Join-Path $root "NoteTagPlugin"

Write-Host "=== 1/3 编译 ==="
Push-Location $pluginDir
try {
    $env:GAME_DIR = $GameDir
    dotnet build -c Release
    if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }
    Remove-Item Env:GAME_DIR
} finally {
    Pop-Location
}

if ($Version -eq "") {
    $match = Select-String -Path (Join-Path $pluginDir "Plugin.cs") -Pattern 'BepInPlugin\("[^"]+", "[^"]+", "([^"]+)"\)'
    if ($match) { $Version = "v" + $match.Matches[0].Groups[1].Value }
}
if ($Version -eq "") { $Version = "v0.0.0" }

Write-Host "=== 2/3 打包 ($Version) ==="
$distDir = Join-Path $root "dist"
$stageDir = Join-Path $distDir "stage"
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item (Join-Path $pluginDir "bin\Release\net6.0\NoteTagPlugin.dll") $stageDir -Force
Copy-Item (Join-Path $pluginDir "Name_Tag.png") $stageDir -Force
Copy-Item (Join-Path $root "README.md") $stageDir -Force

$zip = Join-Path $distDir "NoteTagPlugin-$Version.zip"
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zip -Force
Remove-Item $stageDir -Recurse -Force

Write-Host "=== 3/3 完成 ==="
Write-Host "发布包: $zip"
Get-Item $zip | Select-Object Name, @{N="SizeMB";E={[math]::Round($_.Length/1MB,2)}}
Write-Host ""
Write-Host "下一步：在 GitHub 创建 release（tag 建议 $Version），上传该 zip。"
