# ZED ZONE MOD 合集

基于 BepInEx 6 (IL2CPP) 的代码注入实现，完全绕过官方 `.zedmod` mod 系统的权限限制。
开发文档见 [DEVELOPMENT.md](DEVELOPMENT.md)。

---

## 前置：安装 BepInEx 6（一次性）

所有 MOD 都运行在 **BepInEx 6（IL2CPP x64）** 之上，首次使用需先安装：

1. **下载 BepInEx 6**：前往 [BepInEx GitHub发布](https://github.com/BepInEx/BepInEx/releases) → 找到 `BepInEx 6` → 下载
   `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.XXX.zip`（选最新的版本号）
2. **解压到游戏根目录**：把压缩包内容解压到
   `D:\SteamLibrary\steamapps\common\ZED ZONE\`（与 `ZEDZONE.exe` 同级）
   - 解压后应看到：`BepInEx\` 文件夹、`winhttp.dll`、`doorstop_config.ini`、`dotnet\` 等
3. **启动一次游戏**：首次运行 BepInEx 会分析游戏代码生成 interop 程序集（约 30 秒~1 分钟，弹控制台窗口属正常）
4. **验证安装**：游戏目录出现 `BepInEx\LogOutput.log` 且内容含 `BepInEx 6.0.0-be.XXX - ZEDZONE` 即成功

> 游戏更新后无需重装 BepInEx（自动重新生成 interop）。

### 安装 MOD（各 MOD 的 zip 解压后的 dll）

| MOD | 放置路径 |
|---|---|
| NoteTag（命名牌） | `BepInEx\plugins\NoteTagPlugin\`（含 `Name_Tag.png`） |
| BigFridge（大容量冰箱） | `BepInEx\plugins\BigFridge\` |
| PortableFridge（便携小冰箱） | `BepInEx\plugins\PortableFridgePlugin\`（含 `Portable_Fridge.png`） |

---

# MOD 1：命名牌 (NoteTag) v0.5.4

为 ZED ZONE 游戏添加「命名牌」工具：将命名牌拖放到任意物品上即可为该物品添加**持久化备注**（跟随存档保存），悬停物品时备注以亮黄色显示在物品描述与其他信息之间。

## 功能

- **命名牌物品**（itemId 900000，冲突时自动 +1）
  - 物品名：命名牌 / 描述：为任意物品添加备注
  - 堆叠 32 / 重量 0.01 / 价格 1 / 材料类（Material）
- **制造配方**：木头×1 + 炭×1 = 命名牌×2，徒手制作（`CraftPlatform.byhand`），0 级，制作时间 3 游戏分钟
- **拖放交互**：背包中拖命名牌到任意有物品的格子 → 弹出可调整大小的输入框 → 确定后保存备注并消耗 1 个命名牌
- **备注持久化**：写入 `ItemData` 属性表，随游戏存档自动保存/恢复，按物品实例独立绑定
- **tooltip 展示**：备注以亮黄色插在物品名/描述之后、其他物品信息之前
- **自定义贴图**：经 `ModSpriteRegistry` 注册
- **中英双语**：物品名/描述/tooltip/输入框 UI 跟随游戏语言（简体中文/English）自动切换

### 安装（用户侧）

1. 安装 BepInEx 6（IL2CPP x64）到游戏根目录 `D:\SteamLibrary\steamapps\common\ZED ZONE`
2. 将编译产物 `NoteTagPlugin.dll` 放入 `BepInEx\plugins\NoteTagPlugin\`
3. 将 `Name_Tag.png` 放在同目录（贴图资源）
4. 启动游戏：控制台 `` ` `` 输入 `additem 900000` 获取命名牌测试

---

# MOD 2：大容量冰箱 (BigFridge) v0.2.2

将冰箱（`TerrainObject_Production_Fridge`）内部存储从原版 **(8x16) 格**扩容为 **(22x34) 格**（与皮制背包相同尺寸）。

## 功能

- 新放置的冰箱直接以 22x34 初始化
- **旧存档冰箱自动迁移**：加载存档后插件自动检测并扩容已有冰箱（尺寸随存档保存，保存一次后永久生效）
- **全场景覆盖**：远离冰箱不加载时数据无损；游戏内切换存档、新放置冰箱由低频守护轮询（60s）兜底

### 安装（用户侧）

1. 安装 BepInEx 6（IL2CPP x64）到游戏根目录 `D:\SteamLibrary\steamapps\common\ZED ZONE`
2. 将 `FridgeModPlugin.dll` 放入 `BepInEx\plugins\BigFridge\`
3. 启动游戏即可，无需其他操作（日志出现 `收集完成` 即迁移成功）

### 卸载

删除 `FridgeModPlugin.dll` 即可还原 8x16（已保存为大容量的存档仍保持 22x34，格子超出部分物品仍在，仅不再扩容新容器）。

---

# MOD 3：便携小冰箱 (PortableFridge) v0.3.2

可携带的小冰箱：内置容器（10×8 格，同弹药箱）+ 电瓶供电 + 保鲜，出门在外也能储存并保鲜食物。

## 功能

- **内置容器**：Backpack 10×8 格，右键打开使用
- **电瓶供电**：电池槽接受电瓶（itemId 85），满电约 5 天（1200WH / 240WH 每天）
- **食物保鲜**：有电时自动暂停容器内食物的腐烂计时（无电时正常腐烂）
- **制造配方**：工作台制造——铁块×6 + 铁管×4 + 铜丝×4
- **修复配方**：维修包×1
- **中英双语**：物品名/描述跟随游戏语言（简体中文/English）自动切换

### 安装（用户侧）

1. 安装 BepInEx 6（IL2CPP x64）到游戏根目录 `D:\SteamLibrary\steamapps\common\ZED ZONE`
2. 将 `PortableFridgePlugin.dll` 放入 `BepInEx\plugins\PortableFridgePlugin\`
3. 将 `Portable_Fridge.png` 放在同目录（贴图资源）
4. 启动游戏，工作台制造便携小冰箱即可使用

### 卸载

删除 `PortableFridgePlugin.dll` 与 `Portable_Fridge.png` 即可（已放入容器中的物品会保留在存档里）。

---

## 构建（开发者）

环境：.NET SDK（net6.0 目标，SDK 8+ 可用）、游戏本体（需要其 BepInEx interop 程序集）。

```powershell
# csproj 中 GameDir 指向游戏目录
cd NoteTagPlugin
dotnet build -c Release
# 输出: NoteTagPlugin/bin/Release/net6.0/NoteTagPlugin.dll
```

`tools/ildump` 为开发期辅助工具（MetadataLoadContext 读取游戏 interop 程序集，用于逆向分析，非插件运行时所需）。

## 技术要点

- 游戏引擎：Unity 2023.1.18f1（IL2CPP），物品系统核心为 `ItemManager` / `ItemAttr` / `RecipeData` / `BasicItemUI` / `DescriptionTipPanel`
- 物品/配方/贴图全部为运行时代码注入，无需修改游戏文件
- Harmony patch 目标均为**非 virtual 方法**（IL2CPP 下 patch virtual 方法会导致崩溃）
- 中文 UI 字体复用游戏内置字体（Zpix），IMGUI 动态字体 API 在 IL2CPP 下不可用

## 版本

- NoteTag v0.5.4 —— 命名牌：注册/配方/贴图/拖放/持久化/tooltip 展示/性能优化（v0.5.4 中英双语 + 语言状态缓存优化）
- BigFridge v0.2.2 —— 大容量冰箱：22x34 扩容/旧存档迁移/低频守护轮询
- PortableFridge v0.3.2 —— 便携小冰箱：内置容器/电瓶供电/食物保鲜/性能优化（v0.3.2 中英双语 + 贴图改名 + 语言状态缓存优化）

