using System;
using System.IO;
using HarmonyLib;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.67 存档隔离（方案A：文件名嵌入身份键）。
/// 身份键 = $"{GameData.id}_SlotIndex{GameData.saveSlotIndex}"（与 {gameId}_SlotIndex{slot}.game 同构；
/// GameController.instance.gameData 编译期直访，禁反射读实例字段）。
/// 切换：GameDataManager.LoadGameData / GameController.set_gameData 双 postfix → key 变化才
/// Flush 内存旧表 + Load 新表 + 日志。key=""（主菜单/取不到身份）回退 legacy 全局文件名。
/// 迁移只读兜底：namespaced 缺失但 legacy 存在 → 读 legacy（只读一次），首次 Save 落 namespaced。
/// v0.9.69：脏位守卫（Load 期 SuppressDirty，FlushAll 仅写脏表）＋ 本槽收敛
/// （saveEpoch envelope：游戏内存档即一轮，K=3 轮未被活体观测的记录从本槽剔除）。
/// </summary>
public static class TeleportSaveIdentity
{
    private static string _current = null; // null=未初始化；""=主菜单/未知

    public static string Current => _current ?? "";

    // v0.9.69：本槽 saveEpoch（游戏内存档即一轮）；Load 期脏位抑制（各表 MarkDirty 读取）。
    public static int SlotEpoch { get; private set; } = 0;
    public static bool SuppressDirty = false;
    public const int ConvergeK = 3;

    // 由 GameData 对象派生身份键；null/空 id → ""。
    public static string KeyFromGameData(GameData gd)
    {
        try
        {
            if (gd == null) return "";
            string id = gd.id;
            if (string.IsNullOrEmpty(id)) return "";
            int slot = gd.saveSlotIndex;
            return Sanitize($"{id}_SlotIndex{slot}");
        }
        catch { return ""; }
    }

    // 当前身份（双判空；任一 null → ""）。
    public static string CurrentKey()
    {
        try
        {
            var gc = GameController.instance;
            if (gc == null) return "";
            var gd = gc.gameData;
            if (gd == null) return "";
            return KeyFromGameData(gd);
        }
        catch { return ""; }
    }

    private static string Sanitize(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
        catch { return ""; }
    }

    private static string LegacyPath(string baseFileName)
    {
        try { return Path.Combine(BepInEx.Paths.ConfigPath, baseFileName); }
        catch { return baseFileName; }
    }

    // 写路径：有身份 → namespaced；无身份 → legacy（主菜单不断）。
    public static string SavePath(string baseFileName)
    {
        try
        {
            string key = Current;
            if (string.IsNullOrEmpty(key)) return LegacyPath(baseFileName);
            int dot = baseFileName.LastIndexOf('.');
            string stem = dot >= 0 ? baseFileName.Substring(0, dot) : baseFileName;
            string ext = dot >= 0 ? baseFileName.Substring(dot) : "";
            return Path.Combine(BepInEx.Paths.ConfigPath, $"{stem}_{key}{ext}");
        }
        catch { return LegacyPath(baseFileName); }
    }

    // 读路径：namespaced 存在 → 它；否则 legacy 存在 → 它（只读一次兜底）；否则 namespaced（新档）。
    public static string LoadPath(string baseFileName)
    {
        try
        {
            string ns = SavePath(baseFileName);
            if (File.Exists(ns)) return ns;
            string leg = LegacyPath(baseFileName);
            if (File.Exists(leg) && leg != ns) return leg;
            return ns;
        }
        catch { return LegacyPath(baseFileName); }
    }

    public static void Init()
    {
        try
        {
            _current = CurrentKey();
            Plugin.L.LogInfo($"[TS][SaveId] 初始身份 {(string.IsNullOrEmpty(_current) ? "主菜单/未知(legacy)" : _current)}");
        }
        catch { _current = ""; }
    }

    public static void SwitchTo(string newKey)
    {
        try
        {
            if (newKey == null) newKey = "";
            if (_current == null) { _current = newKey; return; } // Init 前不动作
            if (newKey == _current) return;
            string old = _current;
            int n0 = CountAll();
            // v0.9.68 切换先落盘：_current 仍是旧 key，路径函数仍指旧 namespace；
            // old 为空（主菜单/legacy 态）跳过，legacy 保持 pristine 种子不动。
            int flushed = 0;
            if (!string.IsNullOrEmpty(old) && n0 > 0) flushed = FlushAll();
            ResetAll();
            _current = newKey; // 先切 key，LoadAll 才读新 namespace
            LoadAll();
            int n1 = CountAll();
            Plugin.L.LogInfo($"[TS][SaveId] 身份切换 {(string.IsNullOrEmpty(old) ? "主菜单/未知" : old)}({n0}条{(flushed > 0 ? $"，已落盘旧key{flushed}条" : "")}) -> {(string.IsNullOrEmpty(newKey) ? "主菜单/未知" : newKey)}({n1}条)");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS][SaveId] 切换异常: {ex.Message.Split('\n')[0]}"); }
    }

    // 四表强制落盘（绕过节流；返回落盘条目数）。
    private static int FlushAll()
    {
        int n = 0;
        try { n += TeleportBindingManager.FlushForIdentity(); } catch {}
        try { n += TeleportConsoleSelection.FlushForIdentity(); } catch {}
        try { n += TeleportStationNameManager.FlushForIdentity(); } catch {}
        try { n += TeleportMapManager.FlushForIdentity(); } catch {}
        return n;
    }

    private static int CountAll()
    {
        int n = 0;
        try { n += TeleportBindingManager.CountEntries(); } catch {}
        try { n += TeleportConsoleSelection.CountEntries(); } catch {}
        try { n += TeleportStationNameManager.CountEntries(); } catch {}
        try { n += TeleportMapManager.CountPersisted(); } catch {}
        return n;
    }

    private static void ResetAll()
    {
        try { TeleportBindingManager.ResetForIdentity(); } catch {}
        try { TeleportConsoleSelection.ResetForIdentity(); } catch {}
        try { TeleportStationNameManager.ResetForIdentity(); } catch {}
        try { TeleportMapManager.ResetPersistedForIdentity(); } catch {}
    }

    private static void LoadAll()
    {
        SuppressDirty = true;
        try
        {
            try { TeleportBindingManager.Load(); } catch {}
            try { TeleportConsoleSelection.Load(); } catch {}
            try { TeleportStationNameManager.Load(); } catch {}
            try { TeleportMapManager.ReloadPersisted(); } catch {}
            LoadEpoch();
        }
        finally { SuppressDirty = false; }
    }

    // ===== v0.9.69 本槽 saveEpoch envelope（独立小文件，不进记录体防旧解析器断层） =====
    private static string EpochPath(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key)) return null;
            return Path.Combine(BepInEx.Paths.ConfigPath, $"TeleportSaveEpoch_{key}.json");
        }
        catch { return null; }
    }

    private static void LoadEpoch()
    {
        SlotEpoch = 0;
        try
        {
            string p = EpochPath(Current);
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return;
            string txt = File.ReadAllText(p);
            int ci = txt.IndexOf(':');
            if (ci > 0 && int.TryParse(txt.Substring(ci + 1).Trim().Trim('}', ' ', '"'), out var e) && e > 0)
                SlotEpoch = e;
        } catch {}
    }

    private static void SaveEpochFile()
    {
        try
        {
            string p = EpochPath(Current);
            if (string.IsNullOrEmpty(p)) return;
            File.WriteAllText(p, $"{{\"epoch\":{SlotEpoch}}}");
        } catch {}
    }

    // 游戏内存档即一轮：仅当前槽推进；K 轮未见记录从本槽剔除并强制落盘。
    public static void OnGameSavedForCurrentSlot(GameData __0)
    {
        try
        {
            if (__0 == null || string.IsNullOrEmpty(_current)) return;
            if (KeyFromGameData(__0) != _current) return;
            SlotEpoch++;
            SaveEpochFile();
            int removed = 0;
            try { removed = TeleportMapManager.PruneUnseen(SlotEpoch, ConvergeK); } catch {}
            if (removed > 0)
            {
                try { TeleportMapManager.ForceSavePersisted(); } catch {}
                Plugin.L.LogInfo($"[TS][SaveId] 收敛剔除 {removed} 站（本槽 epoch={SlotEpoch}，K={ConvergeK} 轮未见；仅本槽文件）");
            }
            else Plugin.L.LogInfo($"[TS][SaveId] 本槽存档 epoch={SlotEpoch}（无剔除）");
        }
        catch { }
    }

    // ===== Harmony postfix（__0/__result 位置绑定） =====
    public static void LoadGameDataPostfix(GameData __result)
    {
        try
        {
            if (__result == null) return;
            SwitchTo(KeyFromGameData(__result));
        }
        catch { }
    }

    public static void GameDataSetPostfix(GameData __0)
    {
        try { SwitchTo(KeyFromGameData(__0)); } catch { }
    }

    // SaveGameData(GameData, int, bool)：游戏内存档事件（手动＋自动）。
    public static void SaveGameDataPostfix(GameData __0)
    {
        try
        {
            if (__0 == null) return;
            OnGameSavedForCurrentSlot(__0);
        }
        catch { }
    }

    public static void EnsurePatch(Harmony h)
    {
        try
        {
            var mgr = AccessTools.TypeByName("GameDataManager");
            var load = mgr != null ? AccessTools.Method(mgr, "LoadGameData") : null;
            if (load != null)
            {
                h.Patch(load, postfix: new HarmonyMethod(typeof(TeleportSaveIdentity).GetMethod(
                    nameof(LoadGameDataPostfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                Plugin.L.LogInfo("[TS] 已挂钩 GameDataManager.LoadGameData（存档隔离切换）");
            }
            else Plugin.L.LogWarning("[TS] LoadGameData 挂钩失败（方法未找到）");
            var set = AccessTools.Method(typeof(GameController), "set_gameData");
            if (set != null)
            {
                h.Patch(set, postfix: new HarmonyMethod(typeof(TeleportSaveIdentity).GetMethod(
                    nameof(GameDataSetPostfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                Plugin.L.LogInfo("[TS] 已挂钩 GameController.set_gameData（存档隔离切换）");
            }
            else Plugin.L.LogWarning("[TS] set_gameData 挂钩失败（方法未找到）");
            var save = mgr != null ? AccessTools.Method(mgr, "SaveGameData") : null;
            if (save != null)
            {
                h.Patch(save, postfix: new HarmonyMethod(typeof(TeleportSaveIdentity).GetMethod(
                    nameof(SaveGameDataPostfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                Plugin.L.LogInfo("[TS] 已挂钩 GameDataManager.SaveGameData（本槽收敛 epoch）");
            }
            else Plugin.L.LogWarning("[TS] SaveGameData 挂钩失败（方法未找到）");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS] 存档隔离挂钩异常: {ex.Message.Split('\n')[0]}"); }
    }
}
