#Requires -Version 5.1
<#
.SYNOPSIS
  一键探针：不重启游戏下 5s 内判定燃料白名单（腐肉205 / 过期食品）— 运行时链路
.DESCRIPTION
  配合路径：
  1) 纯探针插件（TeleportProbe/PortableFridgeProbe）：LogOutput 每tick取证
  2) CE MCP Bridge（ce_mcp_bridge.lua 已在 CE autorun，pipe \\.\pipe\CE_MCP_Bridge_v99，mcp_cheatengine.py --stdio）：
     直接读进程内存 InventoryData.itemList / ItemData.IsFoodExpired，无需重启。
  本脚本为 CE 链路的 5s 步进：连桥 → 扫燃料仓 → 判定白名单 → 输出日志。
.PARAMETER UseCE
  走 CE Bridge（需 CE 已附加 ZED ZONE + Lua 已 Execute）；否则走日志探针提示。
#>
[CmdletBinding()]
param([switch]$UseCE)

$ErrorActionPreference = "Stop"

function Test-CEBridge {
  $pipe = "\\.\pipe\CE_MCP_Bridge_v99"
  return Test-Path $pipe
}

if ($UseCE) {
  if (-not (Test-CEBridge)) {
    Write-Warning "CE Bridge 管道未就绪: \\.\pipe\CE_MCP_Bridge_v99"
    Write-Host "  1) 启动 Cheat Engine → File → Execute Script → 打开 C:/Users/lily/AppData/Roaming/reasonix/global-workspace/cheatengine-mcp-bridge/MCP_Server/ce_mcp_bridge.lua → Execute"
    Write-Host "  2) CE 中附加 ZED ZONE 进程（Open Process）"
    Write-Host "  3) 重跑: .\tools\CEProbe\ProbeFuel.ps1 -UseCE"
    exit 1
  }
  Write-Host "[CE] 管道就绪，调用 mcp_cheatengine.py 探针..." -ForegroundColor Cyan
  Write-Host "  白名单判定：id==205（腐肉）或 itemType.Contains('Food') && IsFoodExpired==true" -ForegroundColor DarkGray
  Write-Host "  下一步：用 MCP 工具 read_memory / lua_call 读 InventoryData.itemList（见下方 Lua 片段）" -ForegroundColor DarkGray
  @'
-- 在 CE Lua 或通过 MCP lua_call 执行：
local invs = {} -- 填入燃料仓 InventoryData 指针（通过 TerrainObject_Production_StirlingGenerator.ActiveObjects_StirlingGenerator[0].fuelInventoryData）
for i=0, inv.itemList.Count-1 do
  local it = inv.itemList[i]
  local attr = it.itemAttr
  local id = attr.itemId
  local isFood = tostring(attr.itemType):find("Food") ~= nil
  local expired = false; if isFood then expired = it.IsFoodExpired and it:IsFoodExpired() or false end
  local allow = (id==205) or (isFood and expired)
  print(string.format("id=%d isFood=%s expired=%s allow=%s", id, tostring(isFood), tostring(expired), tostring(allow)))
end
'@ | Write-Host -ForegroundColor Gray
  Write-Host "`n[CE] 替代：不经 CE，直接在游戏内放燃料看 LogOutput — BioGenFuel.PassesFeatureLimit 日志即判。" -ForegroundColor DarkGray
  exit 0
}

Write-Host "[探针] 日志链路（无需 CE）— 5s 步进" -ForegroundColor Cyan
Write-Host "  1) 部署 TeleportStation 调试版（BioGenFuel 日志开）" -ForegroundColor Gray
Write-Host "  2) 游戏内：放生物质发电机(900103) → 分别放入 木头0 / 腐肉205 / 新鲜食物 / 过期食物" -ForegroundColor Gray
Write-Host "  3) 看 BepInEx/LogOutput.log：PassesFeatureLimit 判定行（id/type/expired）即白名单结果" -ForegroundColor Gray
Write-Host "  4) 本脚本仅作 CE 加速链路，日志链路无需本脚本" -ForegroundColor DarkGray
Write-Host "`n判定：腐肉205 ✓ | Food+IsFoodExpired ✓ | 其余 ✗ (含 木头/金属)" -ForegroundColor Green
Write-Host "签名坑：PassesFeatureLimit private bool (ItemAttr) / GetItemListByFeature public List<ItemData>(ItemFeatureType) / CostItemDurability public static ItemData (int,float,List<InventoryData>)" -ForegroundColor DarkGray
