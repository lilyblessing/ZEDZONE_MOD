using System;
using System.Collections.Generic;
using UnityEngine;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.94-diag：生物能重建门一次性诊断采样（只读+日志，零行为改动）。
/// 进世界沉降后，对活体 900103（生物机）+900102（圆盘，对照）各实例打一行日志，
/// 记录原生重建六门输入的实际值。无 Harmony 补丁，不写任何游戏字段，每会话只采样一次。
/// 范式出处：
///   - 世界活跃门：照抄 Plugin.cs RegistrationProbe（GameController.instance + playerCharacter 非空）。
///   - powerSwitchOff：照抄 TeleportConsoleSelection.IsPowered（独立 try/catch，默认 false）。
///   - ProductionData 引用：照抄 TeleportPadTrigger.GetProductionData（objectData 直访 + Reflect 兜底）。
///   - 实例查找：TeleportObjectCache.FindAllById（0.5s TTL 缓存，900103/900102 通用）。
/// </summary>
public static class BiogenGateSampler
{
    private static bool _done = false;
    private static float _firstActiveTime = -1f;

    public static void Tick()
    {
        if (_done) return;
        try
        {
            // 世界活跃门（照抄 Plugin.cs:794-795）。
            bool worldActive = false;
            try { var gcW = GameController.instance; if (gcW != null && gcW.playerCharacter != null) worldActive = true; } catch { }
            if (!worldActive) return;
            float now = 0f;
            try { now = Time.unscaledTime; } catch { return; }
            if (_firstActiveTime < 0f) { _firstActiveTime = now; return; }
            if (now - _firstActiveTime < 60f) return;
            try { Sample(); } catch { }
            _done = true;
        }
        catch { }
    }

    private static void Sample()
    {
        List<TerrainObject> list103 = null;
        List<TerrainObject> list102 = null;
        try { list103 = TeleportObjectCache.FindAllById(900103); } catch { list103 = null; }
        try { list102 = TeleportObjectCache.FindAllById(900102); } catch { list102 = null; }
        int n103 = 0;
        int n102 = 0;
        try { n103 = list103 != null ? list103.Count : 0; } catch { n103 = 0; }
        try { n102 = list102 != null ? list102.Count : 0; } catch { n102 = 0; }
        if (n103 + n102 == 0)
        {
            try { Plugin.L?.LogInfo($"[TS][BioGate] 无活体103/102（103={n103} 102={n102}）"); } catch { }
            return;
        }
        try
        {
            if (list103 != null) foreach (var to in list103) { try { LogOne(to); } catch { } }
        }
        catch { }
        try
        {
            if (list102 != null) foreach (var to in list102) { try { LogOne(to); } catch { } }
        }
        catch { }
    }

    private static void LogOne(TerrainObject to)
    {
        if (to == null) return;
        string attrId = "?";
        try { if (to.attr != null) attrId = to.attr.id.ToString(); } catch { attrId = "?"; }
        string craft = "?";
        try { var od = to.objectData; if (od != null) craft = od.craftFinishFlag.ToString(); } catch { craft = "?"; }
        string destroyedProp = "?";
        try { destroyedProp = to.isDestroyed.ToString(); } catch { destroyedProp = "?"; }
        string destroyedField = "?";
        try { var df = Reflect.Get(to, "destroyedFlag"); if (df != null) destroyedField = Convert.ToBoolean(df).ToString(); } catch { destroyedField = "?"; }
        // ProductionData 引用（照抄 TeleportPadTrigger.GetProductionData 判空形态）。
        object pdObj = null;
        try
        {
            var odx = to.objectData;
            if (odx != null && odx.productionData != null) pdObj = odx.productionData;
        }
        catch { }
        try
        {
            if (pdObj == null)
            {
                var od2 = Reflect.Get(to, "objectData");
                if (od2 != null) pdObj = Reflect.Get(od2, "productionData");
            }
        }
        catch { }
        string prodIdLen = "-1";
        try
        {
            var pdT = pdObj as ProductionData;
            if (pdT != null && pdT.productionObjectId != null) prodIdLen = pdT.productionObjectId.Length.ToString();
        }
        catch { prodIdLen = "-1"; }
        string pdNull = "?";
        try { pdNull = (pdObj == null).ToString(); } catch { pdNull = "?"; }
        string attr98 = "?";
        try { if (to.attr != null) attr98 = to.attr.isProductionObject.ToString(); } catch { attr98 = "?"; }
        string attrA1 = "?";
        try { if (to.attr != null) attrA1 = to.attr.electricConsuming.ToString(); } catch { attrA1 = "?"; }
        string attr9C = "?";
        try { if (to.attr != null) attr9C = ((int)to.attr.productionObjectType).ToString(); } catch { attr9C = "?"; }
        // powerSwitchOff（照抄 TeleportConsoleSelection.IsPowered 范式，读不到记 ?）。
        string psoff = "?";
        try { if (pdObj != null) { var o = Reflect.Get(pdObj, "powerSwitchOff"); if (o != null) psoff = Convert.ToBoolean(o).ToString(); } } catch { psoff = "?"; }
        string watt = "?";
        try { if (to.attr != null) { object w = to.attr.electricWattage; if (w != null) watt = w.ToString(); } } catch { watt = "?"; }
        string pos = "?";
        try { var tr = to.transform; if (tr != null) { var p = tr.position; pos = $"{p.x:F1},{p.y:F1},{p.z:F1}"; } } catch { pos = "?"; }
        try { Plugin.L?.LogInfo($"[TS][BioGate] id={attrId} craft={craft} destroyedProp={destroyedProp} destroyedField={destroyedField} prodIdLen={prodIdLen} pdNull={pdNull} attr98={attr98} attrA1={attrA1} attr9C={attr9C} psoff={psoff} watt={watt} pos={pos}"); } catch { }
    }
}
