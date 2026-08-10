using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace ModRepairInjector;

[BepInPlugin("com.zedzone.modrepairinjector", "ModRepairInjector", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;
    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<Injector>();
        Log.LogInfo("[ModRepairInjector] 已加载 (等待 ItemManager 就绪后注入修复配方)");
    }
}

// ---------- 配置文件模型 ----------
public class RepairConfig
{
    public List<RepairRecipeConfig> recipes { get; set; } = new List<RepairRecipeConfig>();
}

public class RepairRecipeConfig
{
    public int runtimeId { get; set; }
    public float craftTime { get; set; } = 1f;
    public List<RepairItemConfig> items { get; set; } = new List<RepairItemConfig>();
}

public class RepairItemConfig
{
    public int itemId { get; set; }
    public float itemNumber { get; set; }
}

// ---------- 注入逻辑 ----------
public class Injector : MonoBehaviour
{
    private float _timer = 8f;
    private int _tries;
    private RepairConfig _cfg;
    private bool _configLoaded;
    private bool _done;

    private void Update()
    {
        if (_done) return;
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = 5f;

        try
        {
            if (!_configLoaded)
            {
                _cfg = LoadConfig();
                _configLoaded = true;
                if (_cfg == null || _cfg.recipes.Count == 0)
                {
                    Plugin.L.LogWarning("[ModRepairInjector] repair.json 为空或未找到，插件不执行");
                    _done = true;
                    return;
                }
            }

            var mgr = ItemManager.instance;
            if (mgr == null)
            {
                if (++_tries > 12)
                {
                    Plugin.L.LogError("[ModRepairInjector] ItemManager 长时间未就绪，放弃");
                    _done = true;
                }
                return;
            }

            bool allDone = true;
            foreach (var r in _cfg.recipes)
            {
                if (r == null || r.runtimeId <= 0) continue;
                var attr = mgr.GetItemAttrById(r.runtimeId);
                if (attr == null)
                {
                    allDone = false;
                    continue;
                }
                if (TryInject(attr, r)) Plugin.L.LogInfo($"[ModRepairInjector] 已注入修复配方: itemId={r.runtimeId}");
            }

            if (allDone)
            {
                _done = true;
                Plugin.L.LogInfo("[ModRepairInjector] 全部注入完成");
            }
            else if (++_tries > 12)
            {
                _done = true;
                Plugin.L.LogError("[ModRepairInjector] 部分物品长时间未注册，放弃重试");
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[ModRepairInjector] 执行失败: {e}");
            _done = true;
        }
    }

    private bool TryInject(ItemAttr attr, RepairRecipeConfig r)
    {
        try
        {
            if (r.items == null || r.items.Count == 0)
            {
                Plugin.L.LogWarning($"[ModRepairInjector] 配方 {r.runtimeId} 无材料项，跳过");
                return true;
            }

            var rd = new RecipeData();
            rd.recipeItems = new Il2CppSystem.Collections.Generic.List<RecipeItemData>();
            foreach (var it in r.items)
            {
                var ri = new RecipeItemData();
                ri.itemId = it.itemId;
                ri.itemNumber = it.itemNumber;
                rd.recipeItems.Add(ri);
            }
            rd.itemId = attr.itemId;
            rd.outputItemNumber = 1;
            rd.craftPlatform = CraftPlatform.byhand;
            rd.toolType = ToolType.None;
            rd.craftTime = r.craftTime;
            rd.fatigueCost = 0f;
            rd.iqReqiared = 0f;
            rd.craftReqiared = 0f;
            rd.craftDifficultyLevel = 0;

            attr.repairData = rd;

            // 验证
            var check = attr.repairData;
            int cnt = (check != null && check.recipeItems != null) ? check.recipeItems.Count : -1;
            Plugin.L.LogInfo($"[ModRepairInjector] 注入后校验: itemId={attr.itemId} name={attr.itemName_Runtime} repairData.recipeItems.Count={cnt}");
            if (check != null && check.recipeItems != null)
            {
                foreach (var ri in check.recipeItems)
                    Plugin.L.LogInfo($"    recipeItems: itemId={ri.itemId} itemNumber={ri.itemNumber}");
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[ModRepairInjector] 注入失败 (runtimeId={r.runtimeId}): {e}");
            return true; // 视为处理过，避免无限重试
        }
    }

    /// 极简 JSON 解析（配置格式固定，避免依赖 IL2CPP 包装的 Newtonsoft）
    private RepairConfig ParseConfig(string text)
    {
        var cfg = new RepairConfig();
        // 每个配方对象以 {"runtimeId": 开头切分
        var parts = Regex.Split(text, @"(?=\{\s*\""runtimeId\"")");
        foreach (var part in parts)
        {
            var mId = Regex.Match(part, @"\""runtimeId\""\s*:\s*(\d+)");
            if (!mId.Success) continue;
            var r = new RepairRecipeConfig { runtimeId = int.Parse(mId.Groups[1].Value) };
            var mTime = Regex.Match(part, @"\""craftTime\""\s*:\s*([\d.]+)");
            if (mTime.Success) r.craftTime = float.Parse(mTime.Groups[1].Value, CultureInfo.InvariantCulture);
            foreach (Match m in Regex.Matches(part, @"\""itemId\""\s*:\s*(\d+)\s*,\s*\""itemNumber\""\s*:\s*([\d.]+)"))
            {
                r.items.Add(new RepairItemConfig
                {
                    itemId = int.Parse(m.Groups[1].Value),
                    itemNumber = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                });
            }
            if (r.items.Count > 0) cfg.recipes.Add(r);
        }
        return cfg;
    }

    private RepairConfig LoadConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            var path = Path.Combine(dir, "repair.json");
            if (!File.Exists(path))
            {
                Plugin.L.LogWarning($"[ModRepairInjector] 未找到配置文件: {path}");
                return null;
            }
            var json = File.ReadAllText(path);
            var cfg = ParseConfig(json);
            Plugin.L.LogInfo($"[ModRepairInjector] 已读取配置: {cfg?.recipes.Count ?? 0} 条配方");
            return cfg;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[ModRepairInjector] 读取配置失败: {e}");
            return null;
        }
    }
}
