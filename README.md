# ZED ZONE 命名牌 MOD (NoteTag)

为 ZED ZONE 游戏添加「命名牌」工具：将命名牌拖放到任意物品上即可为该物品添加**持久化备注**（跟随存档保存），悬停物品时备注以亮黄色显示在物品描述与其他信息之间。

> 基于 BepInEx 6 (IL2CPP) 的代码注入实现，完全绕过官方 `.zedmod` mod 系统的权限限制。

## 功能

- **命名牌物品**（itemId 900000，冲突时自动 +1）
  - 物品名：命名牌 / 描述：为任意物品添加备注
  - 堆叠 32 / 重量 0.01 / 价格 1 / 材料类（Material）
- **制造配方**：木头×1 + 炭×1 = 命名牌×2，徒手制作（`CraftPlatform.byhand`），0 级
- **拖放交互**：背包中拖命名牌到任意有物品的格子 → 弹出可调整大小的输入框 → 确定后保存备注并消耗 1 个命名牌
- **快捷键**：悬停物品时按小键盘 `+` 打开备注输入框（测试用，不消耗命名牌）
- **备注持久化**：写入 `ItemData` 属性表，随游戏存档自动保存/恢复，按物品实例独立绑定
- **tooltip 展示**：备注以亮黄色插在物品名/描述之后、其他物品信息之前
- **自定义贴图**：经 `ModSpriteRegistry` 注册

## 安装（用户侧）

1. 安装 BepInEx 6（IL2CPP x64）到游戏根目录 `D:\SteamLibrary\steamapps\common\ZED ZONE`
2. 将编译产物 `NoteTagPlugin.dll` 放入 `BepInEx\plugins\NoteTagPlugin\`
3. 将 `Name_Tag.png` 放在同目录（贴图资源）
4. 启动游戏：控制台 `` ` `` 输入 `additem 900000` 获取命名牌测试

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

- v0.4.3 —— 功能完整：注册/配方/贴图/拖放/快捷键/持久化/tooltip 展示
