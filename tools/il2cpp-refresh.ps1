#Requires -Version 5.1
<#
.SYNOPSIS
  ZED ZONE IL2CPP 一键刷新流水线 — GameAssembly + global-metadata → DummyDll/dump.cs/script.json → interop 索引 → Ghidra 准备
.DESCRIPTION
  串起散落的 4 件套，消除签名靠猜：
  1) Il2CppDumper: GameAssembly.dll + global-metadata.dat → DummyDll/ + dump.cs + script.json (+ stringliteral.json)
  2) ildump: BepInEx/interop/Assembly-CSharp.dll (MetadataLoadContext) → interop-index.json (2789类全量)
  3) IlBodyCheck: PEReader 读 interop IL → 交叉验证 fieldAccessor / virtual/final
  4) Ghidra: 导入 GameAssembly + script.py 符号（可选）
  幂等：比对 LastWriteTime，输出已新则跳过；失败有清晰错误码。
.PARAMETER GameDir
  游戏根目录，默认 D:\SteamLibrary\steamapps\common\ZED ZONE
.PARAMETER OutDir
  产物根，默认 <repo>/out/il2cpp (DummyDll/dump.cs 等)
.PARAMETER SkipGhidra
  跳过 Ghidra 工程提示（未安装时用）
.PARAMETER Force
  强制重跑，不做 up-to-date 检查
.EXAMPLE
  .\tools\il2cpp-refresh.ps1
  .\tools\il2cpp-refresh.ps1 -Force
  .\tools\il2cpp-refresh.ps1 -GameDir "D:\SteamLibrary\steamapps\common\ZED ZONE" -SkipGhidra
#>
[CmdletBinding()]
param(
  [string]$GameDir = "D:\SteamLibrary\steamapps\common\ZED ZONE",
  [string]$OutDir = "",
  [switch]$SkipGhidra,
  [switch]$Force
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir -or $OutDir -eq "") { $OutDir = Join-Path $repo "out/il2cpp" }
$toolsDir = Join-Path $repo "tools"
$dummyDir = Join-Path $OutDir "DummyDll"
$il2cppDumperDir = Join-Path $toolsDir "Il2CppDumper"

function Test-UpToDate {
  param([string[]]$Inputs, [string[]]$Outputs)
  if ($Force) { return $false }
  foreach ($o in $Outputs) { if (-not (Test-Path $o)) { return $false } }
  $newestIn = ($Inputs | Where-Object { Test-Path $_ } | ForEach-Object { (Get-Item $_).LastWriteTime } | Sort-Object | Select-Object -Last 1)
  $oldestOut = ($Outputs | ForEach-Object { (Get-Item $_).LastWriteTime } | Sort-Object | Select-Object -First 1)
  if ($null -eq $newestIn -or $null -eq $oldestOut) { return $false }
  return $oldestOut -gt $newestIn
}

function Ensure-Il2CppDumper {
  $exe = Join-Path $il2cppDumperDir "Il2CppDumper.exe"
  if (Test-Path $exe) { return $exe }
  Write-Host "[il2cpp-refresh] Il2CppDumper 未找到，尝试下载 v6.7.46..." -ForegroundColor Yellow
  # 2026-09 修正：官方资产名是 Il2CppDumper-win-v6.7.46.zip（旧名 404）；Invoke-WebRequest 在受限沙箱会 SSL 失败，加 curl 兜底
  $url = "https://github.com/Perfare/Il2CppDumper/releases/download/v6.7.46/Il2CppDumper-win-v6.7.46.zip"
  $zip = Join-Path $env:TEMP "Il2CppDumper-v6.7.46.zip"
  try {
    New-Item -ItemType Directory -Force -Path $il2cppDumperDir | Out-Null
    try {
      Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    } catch {
      Write-Warning "Invoke-WebRequest 失败（沙箱 SSL 常见），改用 curl 重试: $($_.Exception.Message)"
      & curl.exe -sSL --retry 3 -o $zip $url
      if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 100000) { throw "curl 下载也失败" }
    }
    Expand-Archive -Path $zip -DestinationPath $il2cppDumperDir -Force
    # zip 内可能含子目录，扁平化
    $inner = Get-ChildItem $il2cppDumperDir -Recurse -Filter "Il2CppDumper.exe" | Select-Object -First 1
    if ($inner -and $inner.DirectoryName -ne $il2cppDumperDir) {
      Copy-Item (Join-Path $inner.DirectoryName "*") $il2cppDumperDir -Force
    }
  } catch {
    throw "下载 Il2CppDumper 失败: $($_.Exception.Message)`n请手动下载 $url 解压到 $il2cppDumperDir"
  }
  $exe2 = Join-Path $il2cppDumperDir "Il2CppDumper.exe"
  if (-not (Test-Path $exe2)) { throw "Il2CppDumper.exe 仍未找到于 $il2cppDumperDir" }
  return $exe2
}

$gameAssembly = Join-Path $GameDir "GameAssembly.dll"
$globalMeta   = Join-Path $GameDir "ZEDZONE_Data/il2cpp_data/Metadata/global-metadata.dat"
$interopDll   = Join-Path $GameDir "BepInEx/interop/Assembly-CSharp.dll"

foreach ($p in @($gameAssembly, $globalMeta)) {
  if (-not (Test-Path $p)) { throw "缺失输入: $p (GameDir=$GameDir)" }
}
if (-not (Test-Path $interopDll)) { Write-Warning "interop 未生成: $interopDll — 将跳过 interop 索引（首次启动游戏生成 interop 后重试）" }

$dumpCs   = Join-Path $OutDir "dump.cs"
$scriptPy = Join-Path $OutDir "script.py"
$scriptJson = Join-Path $OutDir "script.json"
$dummyAsm = Join-Path $dummyDir "Assembly-CSharp.dll"
$indexJson = Join-Path $OutDir "interop-index.json"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $dummyDir | Out-Null

# ── Step 1: Il2CppDumper ──────────────────────────────────────────────
$step1Out = @($dummyAsm, $dumpCs, $scriptJson)
if (Test-UpToDate -Inputs @($gameAssembly, $globalMeta) -Outputs $step1Out) {
  Write-Host "[1/4] Il2CppDumper 已是最新，跳过 (加 -Force 强制重跑)" -ForegroundColor DarkGray
} else {
  $dumper = Ensure-Il2CppDumper
  Write-Host "[1/4] Il2CppDumper: $gameAssembly + global-metadata → $OutDir" -ForegroundColor Cyan
  # Il2CppDumper 非交互：命令行模式 <exe> <GameAssembly> <global-metadata> <outDir>
  # 若版本要求 config.json 交互，传入 dummy 输入
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $dumper
  $psi.Arguments = "`"$gameAssembly`" `"$globalMeta`" `"$OutDir`""
  $psi.WorkingDirectory = $OutDir
  $psi.UseShellExecute = $false
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $proc = [System.Diagnostics.Process]::Start($psi)
  $proc.StandardInput.WriteLine("`n") # 对 config 提示回车跳过
  $proc.StandardInput.Close()
  $proc.WaitForExit(120000) | Out-Null
  if ($proc.ExitCode -ne 0) {
    Write-Warning "Il2CppDumper 退出码 $($proc.ExitCode)，输出:`n$($proc.StandardOutput.ReadToEnd())`n$($proc.StandardError.ReadToEnd())"
    # 兼容部分版本要求手动选模式，尝试备用：直接把 DummyDll 产物找出来
  }
  # 部分版本把 DummyDll 放在子目录，归一化到 $dummyDir
  $foundDummy = Get-ChildItem $OutDir -Recurse -Filter "Assembly-CSharp.dll" | Where-Object { $_.FullName -like "*DummyDll*" } | Select-Object -First 1
  if (-not (Test-Path $dummyAsm) -and $foundDummy) {
    Copy-Item $foundDummy.FullName $dummyAsm -Force
  }
  if (Test-Path $dummyAsm) { Write-Host "  ✓ DummyDll: $dummyAsm ($([math]::Round((Get-Item $dummyAsm).Length/1MB,1)) MB)" -ForegroundColor Green }
  else { Write-Warning "  ! DummyDll 未生成，请检查 Il2CppDumper 控制台输出" }
  if (Test-Path $dumpCs) { Write-Host "  ✓ dump.cs: $([math]::Round((Get-Item $dumpCs).Length/1KB)) KB" -ForegroundColor Green }
  if (Test-Path $scriptPy) { Write-Host "  ✓ script.py (Ghidra): $scriptPy" -ForegroundColor Green }
}

# ── Step 2: ildump interop 索引 ───────────────────────────────────────
$ildumpDll = Join-Path $toolsDir "ildump/bin/Release/net8.0/ildump.dll"
if (-not (Test-Path $ildumpDll)) {
  $ildumpDll = Join-Path $toolsDir "ildump/bin/Debug/net8.0/ildump.dll"
}
if ((Test-Path $interopDll) -and (Test-Path $ildumpDll)) {
  if (Test-UpToDate -Inputs @($interopDll, $ildumpDll) -Outputs @($indexJson)) {
    Write-Host "[2/4] interop-index 已是最新，跳过" -ForegroundColor DarkGray
  } else {
    Write-Host "[2/4] ildump interop 索引 → $indexJson" -ForegroundColor Cyan
    dotnet $ildumpDll --asm $interopDll --search "" 2>&1 | Out-File $indexJson -Encoding utf8
    Write-Host "  ✓ 索引行数: $((Get-Content $indexJson | Measure-Object -Line).Lines)" -ForegroundColor Green
    Write-Host "  用法: dotnet $ildumpDll --type InventoryData --members | dotnet $ildumpDll --member PassesFeatureLimit" -ForegroundColor DarkGray
  }
} else {
  Write-Host "[2/4] 跳过 interop 索引（缺 ildump 或 interop）" -ForegroundColor DarkGray
}

# ── Step 3: IlBodyCheck 交叉验证 ──────────────────────────────────────
$ilbodyDll = Join-Path $toolsDir "ilbody-check/IlBodyCheck/bin/Release/net8.0/IlBodyCheck.dll"
if (-not (Test-Path $ilbodyDll)) { $ilbodyDll = Join-Path $toolsDir "ilbody-check/IlBodyCheck/bin/Debug/net8.0/IlBodyCheck.dll" }
if ((Test-Path $interopDll) -and (Test-Path $ilbodyDll)) {
  Write-Host "[3/4] IlBodyCheck 交叉验证 (fieldAccessor/virtual)" -ForegroundColor Cyan
  try { dotnet $ilbodyDll 2>&1 | Select-Object -First 80 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray } } catch { Write-Warning "IlBodyCheck 执行失败: $_" }
} else {
  Write-Host "[3/4] 跳过 IlBodyCheck（未编译或无 interop）" -ForegroundColor DarkGray
}

# ── Step 4: Ghidra 准备 ───────────────────────────────────────────────
if ($SkipGhidra) {
  Write-Host "[4/4] 跳过 Ghidra（-SkipGhidra）" -ForegroundColor DarkGray
} else {
  $ghidra = "D:/tools/ghidra/ghidraRun.bat"
  if (Test-Path $ghidra) {
    Write-Host "[4/4] Ghidra 已安装: $ghidra" -ForegroundColor Green
    Write-Host "  导入: File → New Project → Import $gameAssembly (Language: x86:LE:64, Base 0x180000000)" -ForegroundColor DarkGray
    Write-Host "  符号: Window → Script Manager → 载入 $scriptPy (选 $scriptJson / stringliteral.json)" -ForegroundColor DarkGray
    Write-Host "  验证: ProductionManager.UpdateStirlingGenerator VA 0x180929510 / BatteryCharger.UpdateBatteryCharging" -ForegroundColor DarkGray
  } else {
    Write-Host "[4/4] Ghidra 未安装" -ForegroundColor Yellow
    Write-Host "  安装: 下载 https://github.com/NationalSecurityAgency/ghidra/releases (11.x) 解压到 D:/tools/ghidra" -ForegroundColor Yellow
    Write-Host "  依赖: JDK 17+ (https://adoptium.net)，设 JAVA_HOME 并验证 java -version" -ForegroundColor Yellow
    Write-Host "  之后重跑: .\tools\il2cpp-refresh.ps1 -SkipGhidra:`$false" -ForegroundColor DarkGray
  }
  try { java -version 2>&1 | Select-Object -First 2 | ForEach-Object { Write-Host "  java: $_" -ForegroundColor DarkGray } } catch {}
}

Write-Host "`n[il2cpp-refresh] 完成。产物根: $OutDir" -ForegroundColor Green
Write-Host "  DummyDll → ILSpy/dnSpyEx 直接打开 $dummyAsm 查签名（最准）" -ForegroundColor DarkGray
Write-Host "  dump.cs  → 文本搜 InventoryData.PassesFeatureLimit / CostItemDurability" -ForegroundColor DarkGray
Write-Host "  详见 Obsidian: LLM 文档/Reasonix/ZEDZONE/dev/il2cpp-工具链-使用手册.md" -ForegroundColor DarkGray
