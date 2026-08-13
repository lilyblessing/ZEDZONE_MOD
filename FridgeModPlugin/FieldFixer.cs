using System;
using System.Collections.Generic;
using UnityEngine;

namespace FridgeModPlugin;

/// <summary>
/// 轮询收集冰箱库存（读档后冰箱才实例化；游戏读档会重置容器尺寸，故每次启动
/// 都必须重新收集）。收集完成后降频为低频守护（覆盖游戏内切换存档、
/// 新放置冰箱等后续出现 8x16 容器的场景）。频率自适应：前 FastRounds 轮 ActiveInterval，
/// 之后 SlowInterval；库存&gt;0 且连续 StableRoundsRequired 轮完全稳定才进入守护；日志仅变化时输出。
/// </summary>
public class FieldFixer : MonoBehaviour
{
    private const float ActiveInterval = 5f;    // 前 FastRounds 轮（快速覆盖读档）
    private const float SlowInterval = 20f;     // FastRounds 轮之后
    private const float IdleInterval = 60f;     // 低频守护模式
    private const int FastRounds = 8;
    private const int StableRoundsRequired = 3;

    private float _timer = ActiveInterval;
    private int _round;
    private int _lastFridgeCount = -1;
    private int _lastInvCount = -1;
    private int _stableRounds;
    private bool _idle; // 低频守护模式（收集完成后）
    private readonly HashSet<long> _seenFridgePtrs = new();
    private readonly HashSet<long> _seenInvPtrs = new();

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        // 频率：守护模式 IdleInterval；否则前 FastRounds 轮 ActiveInterval（快速覆盖读档），之后 SlowInterval
        _timer = _idle ? IdleInterval : (_round < FastRounds ? ActiveInterval : SlowInterval);
        _round++;

        try
        {
            var all = Resources.FindObjectsOfTypeAll<TerrainObject_Production_Fridge>();
            int fridgeCount = all != null ? all.Length : 0;

            // 集合每轮重建（读档后对象引用需刷新）
            Plugin.FridgeInventories.Clear();
            int invCount = 0;
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var fridge = all[i];
                    if (fridge == null || fridge.objectData == null) continue;

                    // 冰箱自身尺寸：仅首次见到时写入（去重）
                    if (_seenFridgePtrs.Add(fridge.Pointer.ToInt64()))
                    {
                        try { fridge.inventorySize = Plugin.NewSize; } catch { }
                    }

                    foreach (var inv in new[] { fridge.objectData.inventoryData, fridge.objectData.inventoryData2, fridge.objectData.inventoryData3 })
                    {
                        if (inv == null) continue;
                        Plugin.FridgeInventories.Add(inv);
                        // 库存尺寸：仅首次见到时写入（去重）
                        if (_seenInvPtrs.Add(inv.Pointer.ToInt64()))
                        {
                            try { inv.inventorySize = Plugin.NewSize; } catch { }
                        }
                        invCount++;
                    }
                }
            }

            // 稳定性：冰箱数与库存数都稳定才累计（先判断变化再更新基准）
            bool changed = fridgeCount != _lastFridgeCount || invCount != _lastInvCount;
            if (!changed)
                _stableRounds++;
            else
                _stableRounds = 0;

            // 收集完成 → 进入低频守护（不彻底停止，覆盖游戏内换档/新冰箱）
            if (!_idle && invCount > 0 && _stableRounds >= StableRoundsRequired)
            {
                _idle = true;
                _lastFridgeCount = fridgeCount;
                _lastInvCount = invCount;
                Plugin.L.LogInfo($"收集完成(第{_round}轮): 冰箱={fridgeCount} 库存={invCount} 集合={Plugin.FridgeInventories.Count}，进入低频守护({IdleInterval}s)");
                return;
            }

            // 日志降噪：仅数量变化时输出
            if (changed)
            {
                string tag = _idle ? "守护" : $"第{_round}轮";
                Plugin.L.LogInfo($"{tag}: 冰箱={fridgeCount} 库存={invCount} 集合={Plugin.FridgeInventories.Count}");
            }
            _lastFridgeCount = fridgeCount;
            _lastInvCount = invCount;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"字段兜底失败: {e}");
        }
    }
}
