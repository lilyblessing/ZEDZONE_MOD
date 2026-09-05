using System;
using System.Collections.Generic;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.93 读档去重守卫：09-05 更新后原生 ProductionManager.OnLoadGame（VA 0x180999ED0）
/// 循环内对 productionDataIDDic 做无守卫 Dictionary.Add(key=pd.productionObjectId, pd)，
/// 存档体自带重复 productionObjectId（Slot5 档实测 25 种重复）→ 首个重复键抛 ArgumentException →
/// 读档链中道崩，后半设施永不注册。
/// 本 prefix 在原生循环跑之前，把重复 productionObjectId 的多余表项原地剔除（留首个），
/// void 返回永不跳过原生；不碰存档文件（内存去重后原生存档自然越存越干净）。
/// 剔除范围：① GameController.instance.gameData.productionDataList；
/// ② ProductionManager 自身生产表（__instance.productionDataList，若存在且与①非同一引用）。
/// 成员名经 out/il2cpp/dump.cs 核对：
/// GameController.instance（dump.cs:35295）、gameData（dump.cs:35833）、
/// GameData.productionDataList（ProtoMember 12，dump.cs:47377）、
/// ProductionManager.productionDataList（dump.cs:78687）、
/// ProductionData.productionObjectId（dump.cs:78527）。
/// IL2CPP 代理类型一律编译期直访（pd.productionObjectId / gc.gameData / mgr.productionDataList），零反射。
/// </summary>
public static class LoadGuardFix
{
    /// <summary>ProductionManager.OnLoadGame prefix（无参实例方法）：只去重，永不返 false。</summary>
    public static void OnLoadGamePrefix(ProductionManager __instance)
    {
        try
        {
            int src = 0;
            int after = 0;
            int cut = 0;
            var cutGuids = new List<string>();

            // ① 存档体：GameController.instance.gameData.productionDataList
            try
            {
                var gc = GameController.instance;
                var gd = gc != null ? gc.gameData : null;
                var list = gd != null ? gd.productionDataList : null;
                if (list != null)
                {
                    src = list.Count;
                    // var 闭包传下标访问器：不具名 Il2Cpp/BCL List 具体类型亦可编译
                    cut += Dedup(list.Count, (i) => list[i] as ProductionData, (idx) => list.RemoveAt(idx), cutGuids);
                    after = list.Count;
                }
            }
            catch { }

            // ② manager 自身生产表（存在且与①非同一引用才做；同一表重跑本就是 no-op）
            try
            {
                var mgrList = __instance != null ? __instance.productionDataList : null;
                if (mgrList != null)
                {
                    bool sameRef = false;
                    try
                    {
                        var gc2 = GameController.instance;
                        var gd2 = gc2 != null ? gc2.gameData : null;
                        var list1 = gd2 != null ? gd2.productionDataList : null;
                        sameRef = object.ReferenceEquals(list1, mgrList);
                    }
                    catch { sameRef = false; }
                    if (!sameRef)
                    {
                        src += mgrList.Count;
                        cut += Dedup(mgrList.Count, (i) => mgrList[i] as ProductionData, (idx) => mgrList.RemoveAt(idx), cutGuids);
                        after += mgrList.Count;
                    }
                }
            }
            catch { }

            try
            {
                if (cut > 0)
                {
                    string guids = cutGuids.Count > 0 ? string.Join(",", cutGuids.ToArray()) : "-";
                    Plugin.L.LogInfo($"[TS][LoadGuard] 去重 productionObjectId: 源={src} 去后={after} 剔除={cut}（{guids}）");
                }
                else
                {
                    Plugin.L.LogInfo($"[TS][LoadGuard] productionObjectId 无重复（源={src}）");
                }
            }
            catch { }
        }
        catch { }
    }

    /// <summary>原地去重：重复 productionObjectId 留首个，多余表项按下标快照降序剔除（防枚举中修改）。
    /// 空/null ID 跳过不处理；字符串比较 ordinal。返回剔除数，剔除 guid 追加进 cutGuids（最多 5 个）。</summary>
    private static int Dedup(int count, Func<int, ProductionData> get, Action<int> removeAt, List<string> cutGuids)
    {
        try
        {
            if (count <= 1) return 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dupIdx = new List<int>();
            for (int i = 0; i < count; i++)
            {
                ProductionData pd = null;
                try { pd = get(i); } catch { continue; }
                if (pd == null) continue;
                string id = null;
                try { id = pd.productionObjectId; } catch { continue; }
                if (string.IsNullOrEmpty(id)) continue;
                if (!seen.Add(id)) dupIdx.Add(i);
            }
            if (dupIdx.Count == 0) return 0;
            // 降序剔除：下标快照先行，原表后删
            for (int k = dupIdx.Count - 1; k >= 0; k--)
            {
                int idx = dupIdx[k];
                try
                {
                    if (cutGuids != null && cutGuids.Count < 5)
                    {
                        try
                        {
                            ProductionData dp = get(idx);
                            string did = dp != null ? dp.productionObjectId : null;
                            if (!string.IsNullOrEmpty(did)) cutGuids.Add(did);
                        }
                        catch { }
                    }
                }
                catch { }
                try { removeAt(idx); } catch { }
            }
            return dupIdx.Count;
        }
        catch { return 0; }
    }
}
