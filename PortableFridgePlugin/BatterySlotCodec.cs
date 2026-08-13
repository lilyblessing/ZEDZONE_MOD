using System;

namespace PortableFridgePlugin;

/// <summary>
/// 电池槽数据编解码（属性键 BatterySlot0，格式 "电池itemId|电量WH"）：
/// - 读：GetProperty("BatterySlot0") → Split('|') → (batteryId, remaining)
/// - 写：$"{batteryId}|{remaining:F6}" → SetProperty
/// 电池槽由游戏菜单（BatteryBox/BatteryConsuming 特性）维护，插件手动扣电时直接读写该槽。
/// </summary>
public static class BatterySlotCodec
{
    public const string PropertyKey = "BatterySlot0";
    private const char Separator = '|';

    /// <summary>读取电池槽；无电池/格式异常返回 false。</summary>
    public static bool TryRead(ItemData item, out int batteryId, out float remaining)
    {
        batteryId = 0;
        remaining = 0f;
        if (item == null) return false;
        try
        {
            string slot = item.GetProperty(PropertyKey);
            if (string.IsNullOrEmpty(slot)) return false;
            var parts = slot.Split(Separator);
            if (parts.Length < 2) return false;
            batteryId = int.Parse(parts[0]);
            remaining = float.Parse(parts[1]);
            return true;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"读取电池槽失败: {e.Message}");
            return false;
        }
    }

    /// <summary>写入电池槽（手动扣电后更新电量）。</summary>
    public static void Write(ItemData item, int batteryId, float remaining)
    {
        if (item == null) return;
        try { item.SetProperty(PropertyKey, $"{batteryId}{Separator}{remaining:F6}"); }
        catch (Exception e) { Plugin.L.LogWarning($"写电池槽失败: {e.Message}"); }
    }
}
