using System;
using System.IO;
using HarmonyLib;

namespace TeleportStationPlugin;

/// <summary>
/// v0.9.67 存档隔离（方案A：文件名嵌入身份键）。
/// 身份键 = $"{GameData.id}_SlotIndex{GameData.saveSlotIndex}"（与 {gameId}_SlotIndex{slot}.game 同构；
/// GameController.instance.gameData 编译期直访，禁反射读实例字段）。
/// v0.9.70 pin 设计（反编译实证 subagent/data/saveid-hijack-decompile.md）：
/// 真读档三处（GameDataUI_IngameLoad.OnLoadConfirm 0x180689FE0 / 基类 GameDataUI.OnClick 0x18068B5C0（标题栏行）/
/// DeathPanel.LoadLatestSavedGame 0x180B6F1C0）全是裸 +0x38 写，不经过 LoadGameData/set_gameData；
/// LoadGameData 仅读档菜单枚举＋多人房详情 → 不再做切换源（旧双 hook 对真选择全盲）；
/// 进游戏后 InGameController.<AutoSaveCoroutine> 首 tick 以硬编码 slot=5 调 SaveGameData(0x180687520)，
/// 后者原地改活对象 saveSlotIndex（伪 C :182），不建新对象不调 setter → SaveGameData prefix 快照活槽、
/// postfix 写回（仅内存；文件名取参量 local、序列化在函数内同步完成，磁盘行为保持原生），
/// P2-10：prefix 只快照（槽号＋对账键，零全量IO），万能对账 pin 延后到 postfix（活键≠_current 即 pin，
/// 兜底新开局/漏pin，语义不变）；epoch 先快照内存、文件延后写（收敛写盘之后；SwitchTo 兜底刷旧槽）。
/// set_gameData postfix 保留（原生零调用，无害）。
/// P2-10：标题读档改 prefix pin（行邮戳调用前已就绪），消灭“先旧后新”窗口；死亡/新开局数据仅调用后存在，保持 postfix。
/// 切换：真选择事件 pin＋存档对账 → key 变化才 Flush 内存旧表 + Load 新表 + 日志。
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
            try { if (_epochDirty) { _epochDirty = false; SaveEpochFile(); } } catch { } // P2-10：旧槽 epoch 延后写在此落盘（_current 仍指旧 namespace）
            try { _savePendingKey = null; } catch { } // P2-10：切换后 prefix 快照失效，防 stale pin
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
        try { BioGenFuel.ResetForIdentity(); } catch {}
        try { ChargerPadFix.ResetForIdentity(); } catch {}
        try { BatteryChargeFix.ResetForIdentity(); } catch {}
        try { BuildingPadFix.ResetForIdentity(); } catch {}
        try { PadDeployMonitor.ResetForIdentity(); } catch {}
        try { TeleportStationUid.InvalidateAll(); } catch {}
        try { TeleportObjectCache.InvalidateAll(); } catch {}
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
            SlotEpoch++; // P2-10：先快照内存 epoch
            _epochDirty = true;
            int removed = 0;
            try { removed = TeleportMapManager.PruneUnseen(SlotEpoch, ConvergeK); } catch {}
            if (removed > 0)
            {
                try { TeleportMapManager.ForceSavePersisted(); } catch {}
                Plugin.L.LogInfo($"[TS][SaveId] 收敛剔除 {removed} 站（本槽 epoch={SlotEpoch}，K={ConvergeK} 轮未见；仅本槽文件）");
            }
            else Plugin.L.LogInfo($"[TS][SaveId] 本槽存档 epoch={SlotEpoch}（无剔除）");
            try { _epochDirty = false; SaveEpochFile(); } catch { } // P2-10：epoch 文件延后写（收敛写盘之后）
        }
        catch { }
    }

    // ===== v0.9.70 会话 pin＋槽位屏蔽（__0/__1/__instance 位置绑定）=====
    private static int _saveSnapSlot = int.MinValue; // SaveGameData prefix 快照的活槽号
    // P2-10：prefix 零全量IO —— 对账键只快照、延后到 postfix 再 pin；epoch 先快照内存、文件延后写。
    private static string _savePendingKey = null;
    private static bool _epochDirty = false;

    private static void PinSession(string why, string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key) || key == _current) return;
            SwitchTo(key);
            Plugin.L.LogInfo($"[TS][SaveId] 会话pin({why}) -> {key}");
        }
        catch { }
    }

    // set_gameData 兜底（原生零调用，仅外部补丁调用时切换）。
    public static void GameDataSetPostfix(GameData __0)
    {
        try { SwitchTo(KeyFromGameData(__0)); } catch { }
    }

    // 暂停菜单读档确认 prefix：行对象 gameData(+0x20) 即游戏随后裸写装配的真选择。
    public static void OnLoadConfirmPrefix(GameDataUI_IngameLoad __instance)
    {
        try
        {
            if (__instance == null) return;
            PinSession("读档确认", KeyFromGameData(__instance.gameData));
        }
        catch { }
    }

    // 标题栏读档 postfix（基类行，无子类覆写，裸写已发生）：用行邮戳 pin。
    // P2-10 起不再注册（由下方 prefix 接管，保留作幂等兜底）。
    public static void TitleLoadClickPostfix(GameDataUI __instance)
    {
        try
        {
            if (__instance == null) return;
            PinSession("标题读档", KeyFromGameData(__instance.gameData));
        }
        catch { }
    }

    // P2-10 读档瞬间对齐：标题栏行邮戳 prefix pin（与暂停读档确认同理；行对象字段调用前已就绪，
    // 与 postfix 采样同一 gameData，裸写前身份已切，消灭“先旧后新”窗口；原 postfix 语义被完全覆盖）。
    public static void TitleLoadClickPrefix(GameDataUI __instance)
    {
        try
        {
            if (__instance == null) return;
            PinSession("标题读档", KeyFromGameData(__instance.gameData));
        }
        catch { }
    }

    // 死亡读最新档 postfix：裸写已发生，采样活对象。
    public static void LoadLatestPostfix()
    {
        try { PinSession("死亡读档", CurrentKey()); } catch { }
    }

    // 新开局 postfix：采样活对象（新 id 即新命名空间；未就绪则跳过，由存档对账兜底）。
    public static void NewGamePostfix()
    {
        try { PinSession("新开局", CurrentKey()); } catch { }
    }

    // SaveGameData(GameData, int, bool) prefix：只快照（活槽号＋对账键），零全量IO；对账 pin 延后到 postfix。
    // P2-10：prefix 内不再同步 SwitchTo（旧逻辑 FlushAll/LoadAll 四表全量IO 在存档调用内），消灭存档路径卡顿源。
    public static void SaveGameDataPrefix(GameData __0, int __1)
    {
        try
        {
            if (__0 == null) { _saveSnapSlot = int.MinValue; _savePendingKey = null; return; }
            _saveSnapSlot = __0.saveSlotIndex;
            string liveKey = KeyFromGameData(__0); // prefix 时原地翻转尚未发生，活邮戳可信
            _savePendingKey = (!string.IsNullOrEmpty(liveKey) && liveKey != _current) ? liveKey : null;
        }
        catch { }
    }

    // SaveGameData postfix：写回活槽号（屏蔽 slot=5 硬编码的原地翻转，仅内存），再走本槽 epoch。
    public static void SaveGameDataPostfix(GameData __0)
    {
        try
        {
            if (__0 != null && _saveSnapSlot != int.MinValue && __0.saveSlotIndex != _saveSnapSlot)
            {
                int flipped = __0.saveSlotIndex;
                __0.saveSlotIndex = _saveSnapSlot;
                Plugin.L.LogInfo($"[TS][SaveId] 存档槽回写 {flipped} -> {_saveSnapSlot}（自动档硬编码屏蔽，仅内存）");
            }
            _saveSnapSlot = int.MinValue;
            string pending = null; // P2-10：消费 prefix 快照的对账键（存档体执行完后再 pin，对账语义不变）
            try { pending = _savePendingKey; _savePendingKey = null; } catch { pending = null; }
            try { if (!string.IsNullOrEmpty(pending) && pending != _current) PinSession("存档对账", pending); } catch { }
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
            // v0.9.70：LoadGameData 仅菜单枚举/多人房详情，不再做切换源（只拆钩，不再注册）。
            // set_gameData 原生零调用，保留作外部调用兜底。
            var set = AccessTools.Method(typeof(GameController), "set_gameData");
            if (set != null)
            {
                h.Patch(set, postfix: new HarmonyMethod(typeof(TeleportSaveIdentity).GetMethod(
                    nameof(GameDataSetPostfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                Plugin.L.LogInfo("[TS] 已挂钩 GameController.set_gameData（存档隔离切换兜底）");
            }
            else Plugin.L.LogWarning("[TS] set_gameData 挂钩失败（方法未找到）");
            var save = mgr != null ? AccessTools.Method(mgr, "SaveGameData") : null;
            if (save != null)
            {
                h.Patch(save,
                    prefix: new HarmonyMethod(typeof(TeleportSaveIdentity).GetMethod(
                        nameof(SaveGameDataPrefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(TeleportSaveIdentity).GetMethod(
                        nameof(SaveGameDataPostfix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)));
                Plugin.L.LogInfo("[TS] 已挂钩 GameDataManager.SaveGameData（槽位快照回写＋本槽收敛 epoch）");
            }
            else Plugin.L.LogWarning("[TS] SaveGameData 挂钩失败（方法未找到）");
            // v0.9.70 真选择事件 pin：读档确认(prefix行邮戳) / 标题读档 / 死亡读档 / 新开局×2。
            PatchPin(h, typeof(GameDataUI_IngameLoad), "OnLoadConfirm", nameof(OnLoadConfirmPrefix), true, "暂停读档确认");
            PatchPin(h, typeof(GameDataUI), "OnClick", nameof(TitleLoadClickPrefix), true, "标题读档");
            PatchPin(h, AccessTools.TypeByName("DeathPanel"), "LoadLatestSavedGame", nameof(LoadLatestPostfix), false, "死亡读档");
            PatchPin(h, AccessTools.TypeByName("Map_Character_GameConfig"), "StartGame", nameof(NewGamePostfix), false, "新开局");
            PatchPin(h, AccessTools.TypeByName("StoryModeNewGamePanel"), "OnStartClicked", nameof(NewGamePostfix), false, "剧情新开局");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS] 存档隔离挂钩异常: {ex.Message.Split('\n')[0]}"); }
    }

    private static void PatchPin(Harmony h, Type t, string method, string pin, bool asPrefix, string label)
    {
        try
        {
            var m = t != null ? AccessTools.Method(t, method) : null;
            var pm = typeof(TeleportSaveIdentity).GetMethod(pin,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (m != null && pm != null)
            {
                if (asPrefix) h.Patch(m, prefix: new HarmonyMethod(pm));
                else h.Patch(m, postfix: new HarmonyMethod(pm));
                Plugin.L.LogInfo($"[TS] 已挂钩 {t.Name}.{method}（存档隔离pin：{label}）");
            }
            else Plugin.L.LogWarning($"[TS] {label}挂钩失败（方法未找到）");
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[TS] {label}挂钩异常: {ex.Message.Split('\n')[0]}"); }
    }
}
