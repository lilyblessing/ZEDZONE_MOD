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
| 探查工具 | `tools\PortableFridgeProbe\`（便携冰箱可行性探查，F9 食物快照） |
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
14. **官方 mod.json 的 `repairData` 字段（已过时——2026-08 官方更新已支持）**：曾实测 `BaseModData` 及全部 8 个 ModData 子类无该字段，mod.json 写 `repairData` 被静默忽略、始终注入默认配方（56×1 工具），官方 `13_Authoring_Extras.md` 的 "set repairData by hand" 一度是错误/超前描述。**2026-08 游戏更新后 Mod Editor 已支持写入修复配方**，此问题已解决，当时的 BepInEx 运行时注入方案（tools/ModRepairInjector）已回撤。历史记录见本文件 git 历史，勿再使用该方案。
15. **拦截游戏拖拽（DropOn）必须恢复拖拽状态，否则物品被游戏清理**：`BasicItemUI.OnBeginDrag` 会把物品暂存进 `itemdataTemp`（拖拽中）。若 Harmony Prefix 拦截 `DropOn`（return false 跳过放置）而不做处理，`itemdataTemp` 残留 → **关闭背包时游戏清理"未放置的拖拽物品"，整组物品消失**。修复：拦截后调用 `RestoreDraggedItemToSource()`（private，反射）恢复拖拽状态。⚠️ 该调用会**重建格子 UI**（`BasicItemUI` 引用 Pointer 归零 → `!= null` 判 false），后续操作不能用格子引用，要用 ItemData 引用。
16. **拖放期间 `ItemData.inventoryData` 被游戏置 null（归属临时清空）**：`RestoreDraggedItemToSource` 只恢复格子显示、不恢复 inventoryData。此时 `InventoryData.RemoveItem(item, bool)` 因无 inventory 而失败。**正确归属获取**：`格子(BasicItemUI) → inventoryPanel → inventoryData`（物品在面板格子中显示，必然在其 inventory 内）。移除前先定位格子与面板（移除后 itemdata 被清空无法再定位）。

## 5. NoteTag 插件现状（v0.5.4 封版，可作新 mod 模板）

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

## 5.5 版本号策略

所有 MOD 统一使用 **0.x**（早期阶段），不提前上 1.x。各 MOD 现状：
- NoteTag v0.5.4（命名牌；v0.5.4 中英双语 + 语言状态缓存优化）
- BigFridge v0.2.2（大容量冰箱；曾用 1.2.2，2026-08 统一回退为 0.2.2，**仅版本号，代码不变**）
- PortableFridge v0.3.2（便携小冰箱；v0.3.2 中英双语 + 贴图改名 + 语言状态缓存优化）

**英文适配**（2026-08，进行中）：游戏本地化机制见第 9 节；NoteTag/PortableFridge 已加 `Locale` 类（`LanguageRegistry.IsCurrentChinese()` 检测，实时不缓存），物品名/描述按语言填 `itemName_Runtime`/`itemDescription_WithLanguage`，UI/tooltip 文本经 `Locale.T(zh,en)` 取；语言切换由 MonoBehaviour 轮询（2s）检测后调 `ReapplyLanguage()` 重设物品文本（游戏不覆盖 mod 物品，须自管）。BigFridge 无展示文本，仅日志。PortableFridge 贴图已改名 `Portable_Fridge.png`。

## 5.6 大容量冰箱 BigFridge（v0.2.2）

将冰箱（`TerrainObject_Production_Fridge`）内部存储从 (8x16) 扩为 **(22x34)**。三层方案：
1. patch `TerrainObject_Production_Fridge.get_inventorySize`（非 virtual）→ 新冰箱源头
2. patch `InventoryData.get_inventorySize`（非 virtual）→ 旧冰箱/读档恢复路径（UI 读 InventoryData 尺寸而非建筑 getter）
3. `FieldFixer` 轮询：收集冰箱库存（`TerrainObjectData.inventoryData/2/3`）+ 字段兜底；库存数>0 且连续 3 轮稳定 → 完成，转 60s 低频守护（覆盖游戏内换档/新冰箱）

关键机制：**游戏读档会重置容器尺寸**（存档不持久化），故每次启动需重新收集；尺寸随存档保存一次后永久生效（保存后重载立即 22x34，已实测）。

## 5.7 便携小冰箱 PortableFridge（v0.3.2）

内置容器（Backpack 10×8，同弹药箱）+ 电瓶供电 + 保鲜。完整机制：

**物品注册**（`PortableFridgeItem.cs`）：
- `new ItemAttr_Backpack()`（⚠️ Backpack 必须用子类实例，基类会被游戏强转 `ItemAttr_Backpack` 抛 InvalidCastException 导致物品无法生成）
- `inventorySize = (10,8)`；工作台配方（铁块8×6+铁管10×4+铜丝13×4，craftPlatform=workbench）；修复配方（维修包56×1）
- 电池槽：`BatteryBox`（batteryModel=5 接受电瓶85, batteryNumber=1）+ `BatteryConsuming`（wattage=10）特性（itemFeatures + itemFeatureDataDic + itemFeatureConfigDatas 三者都配）

**电池槽机制**（实测解密）：
- 电池槽存在 `ItemData.itemPropertyPairs`：key `BatterySlot0` → value `"电池itemId|电量WH"`（如 `85|1072`）；key `IsSwitchOn` → `"true"/"false"`
- "安装/取出电池"菜单由 **BatteryConsuming 特性**提供（只留 BatteryBox 菜单会消失）
- 游戏只驱动**装备位**设备的自动扣电（手电筒装身上才耗电）；背包内物品不自动扣 → 扣电由插件手动写 `BatterySlot0`

**保鲜机制**（实测解密）：
- 食物过期判定：`当前游戏时间 − ItemData.properties[0] ≥ ItemAttr_Food.perishTime`；`properties[0]` 是**采集时间戳**（游戏天单位，静态不随腐烂变化）
- 保鲜 = 有电时把容器内食物 `properties[0]` 前移（等效暂停腐烂）

**扣电速率标定**（手电筒对照法，已实测）：
- 装备手电筒（wattage=0.75, IsSwitchOn=true）开 0.5 游戏天 → 9V 电池耗 8.99 WH → **1 wattage = 23.97 WH/游戏天**
- 目标 1200WH 电瓶用 5 天 = 240 WH/天 → wattage = 10（插件手动扣 `239.7 WH/天`）

**时间钩子**：`TimeController.AddTime(float)`（增量）+ `ChangeTimeTo(float)`（绝对跳变，睡觉走它）——两者参数单位 = **游戏天**（1f=1天，0.0006≈1游戏分钟；睡觉 12 小时 = +0.5）。`ChangeTimeTo` 需与 `AddTime` 协同维护 `_lastKnownTime` 计算差值。

**性能优化**（两轮）：
- 第一轮：时间推进合并——Postfix 只累计 `_pendingTime`，≥0.1 游戏天批量处理一次（睡眠每秒数十次调用 → 每 0.1 天一次，native 交互降 2-3 个数量级）；移除开发标定日志
- 第二轮：isFood 判定缓存（`Dictionary<int,bool>`，物品定义不变缓存安全）；跳过小冰箱实例缓存（合并后处理频率已极低）

**工具插件**：`tools/PortableFridgeProbe`（探查：F9 食物/电瓶/电池槽/装备栏快照 + BatteryConsuming 物品扫描）。部署目录中的探查插件发布前需停用（.disabled）。

## 6. 官方 mod 修复配方（已解决）

**需求背景**：7.62 弹链 mod（runtimeId=871704）自定义修复配方（弹簧×10）。

**历史**：2026-08 早期版本官方 ModLoader 不支持修复配方——`BaseModData` 及全部 8 个 ModData 子类无 `repairData` 字段，mod.json 写入被静默忽略、始终注入默认配方（56×1 工具）；曾用 BepInEx 运行时注入方案绕过（tools/ModRepairInjector，配置驱动，见 git 历史）。

**现状**：2026-08 游戏更新后 **Mod Editor 已支持写入修复配方**，官方 mod.json 可直接配置，运行时注入方案已回撤（部署目录与仓库源码均已移除）。

> 经验：`ItemAttr.repairData` 是 `RecipeData` **单对象**（含 `recipeItems` 数组，非数组字段）；craftTime 单位=游戏天。这些知识对 mod.json 配方配置仍有参考价值。

## 7. 发布流程

```powershell
.\tools\make-release.ps1 -Mod notetag|bigfridge|all   # 编译 + 打包 dist\<Mod>-vX.Y.Z.zip（dll+资源+README）
# 手动：GitHub → Releases → 新建（tag=vX.Y.Z）→ 上传 zip
```

**仓库公开铁律：游戏 interop 程序集（BepInEx\interop、core、unity-libs）是游戏衍生数据，绝不入库/上传 release。** CI 自动构建方案因此废弃（曾做 build-deps seed，已回滚）。

## 8. 后续规划

- 其他 MOD 归入本仓库：每个 mod 一个子目录（如 `NoteTagPlugin\` 同级），独立 csproj + 独立 BepInEx plugins 目录
- 参考本手册第 3 节逆向知识与第 4 节踩坑记录，可大幅缩短开发周期

## 9. 游戏多语言机制（2026-08 逆向，英文适配依据）

游戏自带完整本地化系统（源语言英文，中文内置编译；`ZEDZONE_Data\StreamingAssets\Localization\Localization_en_to_XX.csv` 提供 de/es/fr/ja/ko/pl/pt/ru 翻译）。关键 API（interop 已验证）：

| 用途 | API |
|---|---|
| 语言枚举 | `GameLanguage`：仅 `SimplifiedChinese` / `English` 两个值 |
| 检测当前语言 | `LanguageRegistry.IsCurrentChinese()`（静态 bool）；`GameSettingsDataManager.instance.LoadGameSettingsData().gameLanguage`（枚举）/ `.languageCode`（字符串，中文下空、英文下 "en"） |
| 语言代码 | `LanguageRegistry.CnCode = "zh-CN"` / `EnCode = "en"`；`ModLocaleManager.ResolveCurrentLangCode(settings)` → "zh_CN"/"en_US" |
| 按语言取文本 | `TextManager.GetText(key, index, GameLanguage)` |
| 官方 mod 多语言 | `ModLocaleManager`：`ApplyLocaleToAttr(modGuid,itemId)` / `ReapplyAllLocales()` / `GameLanguageToLangCode()`；mod 包 `locale/<code>.json` + `mod.json` 的 `primaryLangCode` 兜底 |

**物品名字段语义**（运行时探查实测）：
- `itemName`（基础字段）**恒定中文**（原版物品在英文模式下仍是中文）；`itemName_Runtime`（运行时名）随语言变（中文"木材" / 英文 "wood"）
- `itemDescription`（基础）恒定中文；`itemDescription_WithLanguage` 随语言变（"小块木材" / "small piece of wood"）
- `ItemName`/`ItemDescription` getter 返回当前语言文本；`ItemName_EN`/`ItemDescription_EN` getter 对原版返回英文，**对 mod 物品只 fallback 到 itemName_Runtime**（mod 物品不在本地化表，查不到）

**关键结论（mod 英文适配必须知道）**：
1. 游戏语言切换会重新填充**原版物品**的 `itemName_Runtime`/`itemDescription_WithLanguage`，但**不覆盖 mod 注入物品**（不在官方 mod 系统的 locale 缓存内）——mod 物品文本必须自管
2. 修改 `GameSettingsData.gameLanguage` 对象**不改变**运行时语言（`IsCurrentChinese()` 不变），语言状态由 `LanguageRegistry` 内部管理
3. mod 适配做法：注册时按 `IsCurrentChinese()` 填 `itemName_Runtime`/`itemDescription_WithLanguage`；语言切换由 MonoBehaviour 轮询（2s）检测变化后调 `ReapplyLanguage()` 重设。UI/tooltip 文本用 `Locale.T(zh, en)` 实时取（天然支持切换）
4. `ItemAttr` 所有语言相关字段均为可写属性（interop 里字段=属性，`Reflect.Set` 三级反射可写）
