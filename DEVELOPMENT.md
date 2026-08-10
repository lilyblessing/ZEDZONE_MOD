# ZED ZONE MOD 开发手册

本仓库（ZEDZONE_MOD）的完整开发文档。包含环境、游戏逆向知识、踩坑记录与工作流。
**新会话继续开发前，先通读本文件。**

---

## 1. 项目与环境

| 项 | 值 |
|---|---|
| 游戏 | ZED ZONE（Steam） |
| 游戏目录 | `D:\SteamLibrary\steamapps\common\ZED ZONE` |
| 引擎 | **Unity 2023.1.18f1 (IL2CPP, x64)**，IL2CPP Metadata 29.1 |
| Mod 框架 | **BepInEx 6.0.0-be.785**（IL2CPP x64，自带 CoreCLR = .NET 6.0.7） |
| 插件项目 | `NoteTagPlugin\`（net6.0，SDK 8.0.423 可编译） |
| 运行时注入工具 | `tools\ModRepairInjector\`（官方 mod 修复配方注入，配置驱动） |
| 仓库 | https://github.com/lilyblessing/ZEDZONE_MOD（**公开**） |
| 逆向工具 | `tools\ildump\`（MetadataLoadContext 读游戏 interop 程序集） |
| 发布脚本 | `tools\make-release.ps1`（本地编译打包 → 手动传 GitHub Release） |

关键路径：
- 游戏 interop 程序集：`<游戏>\BepInEx\interop\`（`Assembly-CSharp.dll` = 游戏主逻辑，12MB）
- BepInEx 核心：`<游戏>\BepInEx\core\`
- 插件部署：`<游戏>\BepInEx\plugins\NoteTagPlugin\`（dll + Name_Tag.png）
- 插件日志：`<游戏>\BepInEx\LogOutput.log`

## 2. 插件开发循环（快）

```powershell
# 编译（csproj 默认 GameDir 指向游戏目录，可用 $env:GAME_DIR 覆盖）
cd <repo>\NoteTagPlugin
dotnet build -c Release

# 部署
Copy-Item bin\Release\net6.0\NoteTagPlugin.dll '<游戏>\BepInEx\plugins\NoteTagPlugin\' -Force

# 测试：重启游戏 → 看 LogOutput.log
```

游戏内测试物品：按 **`` ` ``** 打开控制台 → `additem <itemId>`（命名牌 = 900000）。
游戏更新后 BepInEx 会**自动重新生成 interop**（无需手动处理），但类型可能变化需复验。

## 3. 游戏逆向知识（ildump 已验证）

### 3.1 关键类（interop 版本，类型名=属性名）

| 类 | 说明 | 关键成员 |
|---|---|---|
| `ItemManager` | 物品系统核心，单例 `instance` | **`itemAttrDic`**（Dictionary<int,ItemAttr>）、`itemList`、`materialList`、`allRecipeList`（**全是属性不是字段！**）、`GetItemAttrById(int)`、`GetRecipeDatasById(int)`、`GetItemSprite`、`CreateMissingItemAttr` |
| `ItemAttr` | 物品定义 | `itemId`、`stackNumber`、`weight`、`itemPrice`、`itemType`、`toolType`、**`itemName_Runtime`**（直接中文名）、**`itemDescription_WithLanguage`**、`unlockByDefault`、`recipeDataAry`、**`repairData`（RecipeData 单对象，非数组！）** |
| `RecipeData` | 配方（**修复配方也是它**） | `itemId`、`recipeItems`（List<RecipeItemData>）、`outputItemNumber`、`craftPlatform`、`toolType`、`iqReqiared`、`craftReqiared` |
| `RecipeItemData` | 配方材料 | `itemId`、`itemNumber` |
| `BasicItemUI` | 背包格子 UI | `itemdata`、`isHover`、静态 `ActiveObjects`、`itemdataTemp`、`RefreshItemUI()`、`RefreshItemNumber()`、**`DropOn(PointerEventData)`（非 virtual，可安全 patch）**、`OnDrop/OnBeginDrag`（virtual final，**不可 patch**） |
| `DescriptionTipPanel` | 物品信息框（tooltip）单例 | **`ShowDescription(RectTransform targetRect, string information, Vector2 dir)`**、`informationText`（Text）、`targetRect`、`ClosePanel()`、`Update()` |
| `ItemData` | 物品实例数据 | `itemId`、`itemNumberFloat`、**`GetProperty/SetProperty(string,string)`**（随存档持久化！）、`Pointer`（IL2CPP 实例指针）、`inventoryData`、`GetItemName()` |
| `InventoryData` | 背包数据 | **`RemoveItem(ItemData, bool)`**、`RemoveItems`、**`inventorySize`（Vector2Int，非 virtual getter）** |
| `ModSpriteRegistry` | mod 贴图注册（静态） | **`Register(int itemId, string slot, Sprite)`**（slot="Main"）、`GetMain(int)`、`IsModItem(int)` |
| `TerrainObject_Production_Fridge` | 冰箱（生产型建筑） | `inventorySize`（Vector2Int，非 virtual getter；**UI 不读它**）、制冷逻辑 `UpdateRefrigeration`/`ApplyColdCredit` |
| `TerrainObjectData` | 建筑数据（`TerrainObject.objectData`） | **`inventoryData`/`inventoryData2`/`inventoryData3`**（建筑库存都在这！） |

### 3.2 关键枚举值
- `ItemType.Material = 0`（材料类）
- `ToolType.None = 0`（徒手）
- `CraftPlatform.byhand = 0`（徒手制造）

> ⚠️ **`RecipeData.craftTime` 单位 = 游戏天**（`1f = 24 小时 = 1440 游戏分钟`）！3 游戏分钟 = `3f/1440f`。曾误设 1f 导致配方显示制作一整天。

### 3.3 物品 ID 规则（官方 mod 文档）
- 原版物品 ID：**0–99999**；MOD 物品 ID：**100000–999999**（modGuid 哈希，冲突 +1）
- **木头 = 0，炭 = 6**；命名牌占用 900000（冲突自动 +1）
- 官方文档仓库：`github.com/IndieDev-LevenLiu/zedzone`（mod-docs/docs/en/，含完整物品 ID 表、配方/音频枚举等）

### 3.4 tooltip 显示链路
`BasicItemUI.ShowDescriptionTip()` → `DescriptionTipPanel.instance.ShowDescription(rect, 拼接好的信息字符串, dir)` → `informationText.text = 字符串`。
信息字符串结构：物品名 → 描述 →（空行分隔）→ 其他信息。备注插入点 = 第一个空行之后。
游戏每次刷新（`RefreshHoverDescriptionTip`）会重新调用 ShowDescription 重置文本。

## 4. ⚠️ 踩坑记录（全是血泪）

1. **IL2CPP 下 `Font.CreateDynamicFontFromOSFont` 不存在**（API 被裁剪，运行时 `Method not found`）→ 改用**游戏自带字体**：`DescriptionTipPanel.instance.informationText.font`（Zpix 点阵中文字体），赋给 IMGUI 每个 GUIStyle。
2. **Il2CppInterop 把类字段生成为属性（PROP）**：`GetField("itemAttrDic")` 返回 null！→ 用 `Reflect` 工具类按 **字段 → 属性 → set_方法** 三级查找（`NoteTagPlugin\Reflect.cs`）。
3. **HarmonyX patch IL2CPP 的 virtual/final 方法会崩溃**（DMD NullReferenceException，游戏直接启动失败/运行崩）：`OnDrop`/`OnBeginDrag` 都是 virtual final → **只 patch 非 virtual 方法**。`DropOn`（非 virtual）、`ShowDescription`/`Update`（非 virtual）可 patch。
4. **Harmony 反射自己的 Postfix 方法**必须带 `BindingFlags.NonPublic | Static`（否则 GetMethod 返回 null → HarmonyMethod(null) 抛 ArgumentNullException）。
5. **interop 的 `GUI.Window` 委托 `WindowFunction` 转换失败**（IL2CPP 委托兼容问题）→ 用 `GUI.BeginGroup(rect)` + 标题栏手动拖动（MouseDrag + delta）+ 右下角手动 resize。
6. **`Resources.FindObjectsOfTypeAll(Type)` 需要 `Il2CppSystem.Type`**（编译报错）→ 用泛型版 `FindObjectsOfTypeAll<Font>()`。
7. **IMGUI 默认 skin 各 style 自带字体引用**：只设 `GUI.skin.font` 不生效（style 级 font 优先）→ 必须显式给每个 GUIStyle 赋 `.font`。
8. **`itemdata = null` 后调 `RefreshItemUI()` 必 NRE**（BasicItemUI.cs:148）→ 移除物品用 `inventoryData.RemoveItem(item, true/false)`，之后若残留图标再调所属面板 `Refresh()` 兜底。
9. **`DropOn` 的 `this` 是源格子（拖拽起始格）不是目标格子**：目标要遍历 `PointerEventData.hovered` 找 `GetComponent<BasicItemUI>()` 并排除源自身。拦截命名牌拖放用 `Prefix 返回 false`（跳过游戏放置）。
10. **单条目缓存必须原子更新键和值**：P0-1 优化曾因"目标指针变了但备注值没清"导致备注错绑到所有物品——目标变化时须同时重置值缓存。
11. **`ItemData.Pointer`（IL2CPP 对象指针）在重载存档后变化**：不能用它做持久化 key → 备注写入 `ItemData.SetProperty("notetag_v1", text)`（物品属性**随游戏存档序列化**，重载自动恢复，且按物品实例独立绑定）。
12. **mod 物品注册注入 `itemAttrDic`/`itemList`/`materialList`/`allRecipeList`** 用反射调用 `Add`/`ContainsKey`；贴图经 `ModSpriteRegistry.Register(id, "Main", sprite)`；`Texture2D.LoadImage(byte[])` + `Sprite.Create(tex, rect, pivot)`。
13. **建筑容器（冰箱等）的库存存在 `TerrainObjectData.inventoryData/2/3`**，UI 打开面板读的是 **InventoryData 的尺寸**而不是建筑自身的 `inventorySize` getter——改容器容量要 patch `InventoryData.get_inventorySize`（非 virtual）或直接改 InventoryData 属性；旧存档容器读档后尺寸恢复原值，需轮询收集 + patch 双保险。
14. **官方 mod.json 没有 `repairData` 字段（踩坑重灾区）**：`BaseModData` 及全部 8 个 ModData 子类均无该字段，mod.json 里写 `repairData` 会被 ModLoader **静默忽略**，始终注入默认修复配方（56×1 工具）。官方文档 `mod-docs/docs/en/13_Authoring_Extras.md` 的 "To override, set repairData in mod.json by hand" 是**错误/超前**的（Mod Editor 确实没暴露该字段，但手写也不生效）。→ 修复配方只能 BepInEx 运行时注入：直接设置 `ItemAttr.repairData = RecipeData{ recipeItems=[…], craftPlatform=byhand, toolType=None, craftTime=… }`（见第 6 节 ModRepairInjector）。

## 5. NoteTag 插件现状（v0.5.2 封版，可作新 mod 模板）

```
Plugin.cs           BepInPlugin 入口：AddComponent<NoteTagUI> + Harmony patch
NoteTagUI.cs        IMGUI 输入框（可拖动/调整大小/取消确定）+ 延迟注册命名牌
TooltipPatcher.cs   Harmony patch ShowDescription + Update（tooltip 插入亮黄色备注）
DropPatch.cs        Harmony patch DropOn（命名牌拖放 → 弹输入框 → 消耗 1 个）
NameTagItem.cs      物品/配方/贴图注册（itemId=900000 起，反射注入）
NoteTagStore.cs     备注持久化（ItemData.SetProperty）
Reflect.cs          字段→属性→set_ 三级反射读写
```

性能优化已做：tooltip 目标单条目缓存（`targetRect.Pointer → item → note`，目标不变零遍历零 native）、`EnsureStyles` 一次性初始化、已移除开发期探查与快捷键功能。

## 6. 官方 mod 修复配方注入（ModRepairInjector）

### 6.1 背景与根因（2026-08 排查，已实测确认）

**需求**：7.62 弹链 mod（`Mods\Magazine\391931de-80af-42ae-b601-bd50cc0bfb17.zedmod`，runtimeId=871704）自定义修复配方（弹簧×10）。

**根因链条**（每步都有运行时证据）：
1. `ItemAttr(871704).repairData` 非 null 且内容 = **56×1（工具）** = 官方默认注入配方（`[ModHandler_Magazine]` 日志可见 "repairData is empty... inject default"）
2. 对照原版物品（休闲服）：`repairData.recipeItems[0] = 布料(30)×3`，形状 = `RecipeData` **单对象**（含 `recipeItems` 数组），证明结构正确
3. 用 il2cpp 反射（ildump）检查：`BaseModData` 及全部 8 个 ModData 子类（Material/Food/Clothing/RangedWeapon/Magazine…）**都没有 repairData 字段**
4. **结论**：mod.json 的 `repairData` 不是合法字段 → 被静默忽略 → 永远触发默认配方。官方 `13_Authoring_Extras.md` 该段是**文档错误**（超前的功能描述）

### 6.2 方案：BepInEx 运行时注入（已实测 OK）

官方 mod 系统不支持 → 用 BepInEx 插件在游戏加载 mod 后**直接改写内存中的 `ItemAttr.repairData`**，与原版物品修复走同一机制。

```csharp
// 核心逻辑（tools/ModRepairInjector/Plugin.cs，配置驱动）
var attr = ItemManager.instance.GetItemAttrById(runtimeId);
var rd = new RecipeData { itemId = runtimeId, craftPlatform = CraftPlatform.byhand,
                          toolType = ToolType.None, craftTime = cfg.CraftTime };
foreach (var it in cfg.Items) rd.recipeItems.Add(new RecipeItemData { itemId = it.ItemId, itemNumber = it.ItemNumber });
attr.repairData = rd;   // setter 可用，直接赋值
```

### 6.3 配置（不用重新编译）

`BepInEx\plugins\ModRepairInjector\repair.json`：

```json
{ "recipes": [ { "runtimeId": 871704, "craftTime": 1.0,
  "items": [ { "itemId": 35, "itemNumber": 10.0 } ] } ] }
```

- 改材料/数量：改 `items`；加其他 mod 物品：复制一个块
- 注入后自动校验并打印 `[ModRepairInjector]` 日志（含 recipeItems 内容）

> ⚠️ 依赖 `ItemManager.instance` 就绪（延迟几秒注册）；`runtimeId` 从 mod.json 查（非 itemId）

## 7. 发布流程

```powershell
.\tools\make-release.ps1 -Mod notetag|bigfridge|all   # 编译 + 打包 dist\<Mod>-vX.Y.Z.zip（dll+资源+README）
# 手动：GitHub → Releases → 新建（tag=vX.Y.Z）→ 上传 zip
```

**仓库公开铁律：游戏 interop 程序集（BepInEx\interop、core、unity-libs）是游戏衍生数据，绝不入库/上传 release。** CI 自动构建方案因此废弃（曾做 build-deps seed，已回滚）。

## 8. 后续规划

- 其他 MOD 归入本仓库：每个 mod 一个子目录（如 `NoteTagPlugin\` 同级），独立 csproj + 独立 BepInEx plugins 目录
- 参考本手册第 3 节逆向知识与第 4 节踩坑记录，可大幅缩短开发周期
