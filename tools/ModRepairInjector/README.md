# ModRepairInjector

BepInEx 运行时修复配方注入工具 —— 解决**官方 mod 系统不支持自定义修复配方**的问题（`mod.json` 没有 `repairData` 字段，详见仓库 `DEVELOPMENT.md` 第 6 节 / 踩坑第 14 条）。

## 用法

1. 编译：`dotnet build -c Release`
2. 部署到 `<游戏>\BepInEx\plugins\ModRepairInjector\`：
   - `ModRepairInjector.dll`
   - `repair.json`（配方配置）
3. 重启游戏 → 打开物品修理界面验证
4. 日志搜 `[ModRepairInjector]`（注入 + 校验结果）

## repair.json 格式

```json
{
  "recipes": [
    {
      "runtimeId": 871704,        // 目标 mod 物品 runtimeId（见该 mod 的 mod.json）
      "craftTime": 1.0,           // 制作时间（游戏分钟制，1f = 24 游戏小时，见 DEVELOPMENT.md 3.2）
      "items": [
        { "itemId": 35, "itemNumber": 10.0 }   // 弹簧 ×10
      ]
    }
  ]
}
```

- 改材料/数量：编辑 `items` 数组
- 添加其他 mod 物品：复制一个 `{...}` 块并改 `runtimeId`
- 修改后无需重新编译，重启游戏即生效

## 原理

官方 ModLoader 在加载 mod 时若检测到空的 repairData 会注入默认配方（56×1 工具）。
本插件在游戏加载完 mod 后，用反射直接改写内存中的 `ItemAttr.repairData`：

```csharp
var attr = ItemManager.instance.GetItemAttrById(runtimeId);
attr.repairData = new RecipeData { recipeItems = [...], craftPlatform = byhand, toolType = None, ... };
```

与原版物品的修复配方走完全相同的运行时机制，实测生效。
