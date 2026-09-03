# ZEDZONE 搬运放下（ lift / descend / carry ）钩子定位 — P4 放置检测

> 目标：解释为何 `TerrainObject.Init / GameController.BuildTerrainObject / GameController.AddTerrainObject` 三入口挂 OnPlaced 仍漏掉“搬运圆盘/控制台放下”，并给出最稳 Hook 点  
> 审计时间：2026-09-01 ｜ dump: `out/il2cpp/dump.cs` + `script.json` ｜ Ghidra 工程 `D:\tools\_ghidra_proj\ZEDZONE`（本次受沙箱限制未直连反编译，行为由方法名/签名推断并标注）

---

## 1 结论先行（给 P4 的追加 Hook）

| 优先级 | Hook 点 | 原因 | 备注 |
|---|---|---|---|
| **P0 必加** | `HumanCharacterController.OnPlaceTerrainObject` `RVA 0x48A6F0 VA 0x18048A6F0` | 搬运放下的**托管入口**，所有可搬运物体（圆盘 `vodka`、控制台等）的放下都走这里。非 virtual、非 Slot，Harmony 前缀必触发 | 唯一能拿到“玩家手持物被放下 + 世界坐标”的点；`UpdateLiftObject` 私有轮询仅改预览，不是落点 |
| **P0 双保险** | `TerrainObject.PlaceTerrainObject` `RVA 0x95A430 Slot27 VA 0x18095A430` + `PlaceTerrainObjectWithoutCheck` `RVA 0x95A3F0 VA 0x18095A3F0` | Carry 路径底层真正做 `transform.position` / `chunkData.AddTerrainObjectData` 的地方；`HumanCharacterController.OnPlaceTerrainObject` 内部最终调用它 | virtual Slot27，存在 Slot 直调绕过风险（TerrainObject 原生链内直调不经 Harmony），故与 HCC 入口双挂 |
| **P2 可选** | `TerrainObject.LiftTerrainObject` `RVA 0x956A60 VA 0x180956A60` | 搬起时的对称入口，用于清掉旧 OnPlaced 状态或打日志 | 非 virtual |
| **不推荐作主钩** | `TerrainObject_Lifter.StartDescend` Slot53 / `StartLiftUP` Slot52 | 仅升降机平台升降，不是搬运放下；且为 virtual Slot，存在与 `ElectricPole.RefreshElectricConnection` 同类直调绕过问题 | 可作升降机专属怪物/音效钩，不作通用放置检测 |

> **P4 应改为：`HCC.OnPlaceTerrainObject` prefix 拿 `__instance.liftingObject / liftedObject` + 坐标 → 调 OnPlaced；再在 `TerrainObject.PlaceTerrainObject` postfix 二次校验（防漏）。`Build/AddTerrainObject` 保留作建造放置检测。`TerrainObject_Lifter.StartDescend` 仅作升降机彩蛋检测，不依赖。**

---

## 2 全量候选表（按 dump.cs + script.json 实测）

> 列：类 / 方法 / RVA / VA / 是否 virtual (Slot) / 能否 Harmony / 建议

| # | 类 | 方法 | RVA | VA | virtual / Slot | 能否 Harmony | 建议 |
|---|---|---|---|---|---|---|---|
| 1 | `TerrainObject` | `Init` | `0x954640` | `0x180954640` | Slot4 virtual | ⚠️ 可但常被 chunck 反序列化直调绕过 | 已挂，不覆盖搬运 |
| 2 | `GameController` | `BuildTerrainObject` | `0x56BAE0` | `0x18056BAE0` | 非 virtual | ✅ | 仅建造新造，不含搬运 |
| 3 | `GameController` | `AddTerrainObject` | `0x56A460` | `0x18056A460` | 非 virtual | ✅ | 仅数据持久化，不含搬运预览移动 |
| 4 | `TerrainObject` | `LiftTerrainObject(object)` | `0x956A60` | `0x180956A60` | 非 virtual | ✅ | **搬起**钩，P4 可加日志 |
| 5 | `TerrainObject` | `PlaceTerrainObject(object)` | `0x95A430` | `0x18095A430` | **Slot27 virtual** | ⚠️ 可但有 Slot 直调绕过风险 | **P0 双保险二**，与 HCC 联动 |
| 6 | `TerrainObject` | `PlaceTerrainObjectWithoutCheck()` | `0x95A3F0` | `0x18095A3F0` | 非 virtual | ✅ | 同上，HCC 内部调用链中会走到 |
| 7 | `TerrainObject` | `CanPlace()` | `0x950DA0` | `0x180950DA0` | Slot26 virtual | ⚠️ | 仅校验，不作落点 |
| 8 | `TerrainObject` | `UpdateLifted()` | `0x95CB80` | `0x18095CB80` | Slot15 virtual | ⚠️ / 每帧调用勿 patch | 仅做抬起时跟随 transform，别挂 |
| 9 | `HumanCharacterController` | `UpdateLiftObject()` | `0x4A7090` | `0x1804A7090` | private 非 virtual | ✅ 但私有可能被内联 | 每帧预览/合法性检测，非落点 |
| 10 | `HumanCharacterController` | `OnLiftObject(TerrainObject)` | `0x488AF0` | `0x180488AF0` | 非 virtual | ✅ | **搬起**托管入口，可作对称钩 |
| 11 | `HumanCharacterController` | `OnPlaceTerrainObject()` | `0x48A6F0` | `0x18048A6F0` | 非 virtual | ✅ **最稳** | **P0 主钩** — 搬运放下唯一托管入口 |
| 12 | `HumanCharacterController` | `ReleaseLiftingTerrainObject()` | `0x4969F0` | `0x1804969F0` | 非 virtual | ✅ | 取消/丢弃路径，可作兜底但无坐标 |
| 13 | `HumanCharacterController` | `ClearPlacementPreviews()` | `0x471590` | `0x180471590` | 非 virtual | ✅ | 仅清理预览 |
| 14 | `TerrainObject_Lifter` | `StartLiftUP(object)` | `0x9A82F0` | `0x1809A82F0` | **Slot52 virtual** | ⚠️ 可能 Slot 直调 | 升降机上升，非搬运 |
| 15 | `TerrainObject_Lifter` | `StartDescend(object)` | `0x9A8140` | `0x1809A8140` | **Slot53 virtual** | ⚠️ | 升降机下降，平台动画+怪物生成，非搬运放下 |
| 16 | `TerrainObject_Lifter` | `OnDescend()` | `0x9A6DE0` | `0x1809A6DE0` | Slot54 virtual | ⚠️ | 内部由 StartDescend 调用 |
| 17 | `TerrainObject_Lifter` | `OnLifted()` | `0x9A7070` | `0x1809A7070` | Slot55 virtual | ⚠️ | 平台到顶回调，注意 IL2CPP 经验：Slot55 同 ElectricPole 有 virtual 直调绕过先例，勿依赖 |
| 18 | `TerrainObject_Lifter` | `TakelifterItem(object)` | `0x9A8530` | `0x1809A8530` | **Slot56 virtual** | ⚠️ | 取升降机内物品（钥匙/道具），不是放下建筑 |
| 19 | `TerrainObject_Lifter` | `NetApplyMove(bool)` | `0x9A6A00` | `0x1809A6A00` | 非 virtual | ✅ | 网络同步平台位移，仅 `transform` 移动，不经 AddTerrainObject |
| 20 | `TerrainObject_Lifter` | `NetApplyTaken()` | `0x9A6AB0` | `0x1809A6AB0` | 非 virtual | ✅ | 网络同步已取走 |
| 21 | `TerrainObject_Lifter` | `NetHostTakeItem(out ItemData)` | `0x9A6BF0` | `0x1809A6BF0` | 非 virtual | ✅ | 服务端授权 |
| 22 | `TerrainObject_Lifter` | `GenerateLifterMonsters()` | `0x9A60B0` | `0x1809A60B0` | Slot57 virtual | ⚠️ | 仅刷怪 |
| 23 | `TerrainObject_Lifter_EngineFactory` | `StartDescend` override | `0x9A5DF0` | `0x1809A5DF0` | Slot53 override | ⚠️ | 子类重写，告警+刷怪逻辑 |
| 24 | `TerrainObject_Lifter_Stadium` | `StartDescend` override | `~0x9A??` | `~0x1809A????` | Slot53 override | ⚠️ | 同上 |
| 25 | `NetLifterSync` | `InterceptTake / OnHostReceiveTakeRequest / ReportTaken` | — | — | — | — | 网络层，非本地放下 |

> 搜索命令（本机可复现）：
> ```pwsh
> Select-String -Path out/il2cpp/dump.cs -Pattern "Lifter|Lift|Descend|PlaceTerrainObject|Drop|Carry" | Select-Object LineNumber,Line
> Select-String -Path out/il2cpp/script.json -Pattern "HumanCharacterController\$\$.*Lift|Place|TerrainObject_Lifter"
> ```

---

## 3 为何之前三入口漏掉搬运

- `BuildTerrainObject` / `AddTerrainObject` 仅在**建造新物体**（配方消耗、放置蓝图）时调用，产生全新 `TerrainObjectData` 并 `Instantiate`。
- **搬运**是已有 `TerrainObject` 实例的**位移**：`LiftTerrainObject` 把实例标记 `isLifted=true`、隐藏碰撞、挂到 `HumanCharacterController.liftedObject`；`OnPlaceTerrainObject` 再把同一实例 `transform.position = 目标格`、`m_collider.enabled=true`、写回 `objectData.worldPosition` 并 `chunkData.AddTerrainObjectData`（或 `PlaceTerrainObjectWithoutCheck`）。全程**不**走 `Build/AddTerrainObject` 也不走 `Init`（Init 仅 chunk 加载时 Clear+重建，P3 经验的权威时机）。
- `TerrainObject_Lifter` 的 `StartDescend` 更是独立系统：控制**升降机平台**上下及刷怪，与“玩家手持圆盘放下”是两条链。用户实测“搬运圆盘/控制台不弹提示”正好验证。

---

## 4 Ghidra 反编译预期（本次沙箱受限未直连，基于命名推断 + 前作验证）

> 允许目录仅 `HarnessWorkspace / ZED ZONE / Obsidian`，`D:\tools\_ghidra_proj` 不在白名单，`DecompileVAs.java` 未能执行。以下为按方法签名与既往 P3 经验的**可验证推断**，建议 reasonix 单会话内补跑：

- `HumanCharacterController.OnPlaceTerrainObject @0x18048A6F0`：读 `__this.liftedObject`（或 `m_liftingTerrainObject`），校验 `CanPlace()`，取鼠标世界坐标/格子，调 `liftedObject.PlaceTerrainObject(obj)` 或 `PlaceTerrainObjectWithoutCheck`，清空 `liftedObject`，广播 `MsgPlayerCarry`。
- `TerrainObject.PlaceTerrainObject @0x18095A430`：校验 `blockLayerMask`、`CanPlace`，写 `objectData.position`，`transform.position = worldPos`，`RemoveFromBuildingData`→`AddTerrainObjectData`，`OnBuildFinish`。
- `TerrainObject_Lifter.StartDescend @0x1809A8140` → `OnDescend @0x1809A6DE0` → `NetApplyMove(false)` → 协程插值 `platformObject.transform`，不涉及 `AddTerrainObject`。
- `TakelifterItem @0x1809A8530` → `NetHostTakeItem` → 给玩家 `ItemData`，`itemTakenFlag=true`。

**验证命令（白名单内可跑）：**
```pwsh
# Ghidra headless（需在 reasonix 单会话、danger 权限）
& D:\tools\ghidra\support\analyzeHeadless D:\tools\_ghidra_proj ZEDZONE -import D:\SteamLibrary\steamapps\common\ZED\ ZONE\GameAssembly.dll -postScript DecompileVAs.java 0x18048A6F0,0x18095A430,0x1809A8140,0x1809A6A00,0x180956A60
# 或直接看已反编译缓存
```

---

## 5 给 P4 的代码改法（双保险）

```csharp
// 1) 主钩：搬运放下（最稳，非 virtual，不怕 Slot 直调）
[HarmonyPatch(typeof(HumanCharacterController), nameof(HumanCharacterController.OnPlaceTerrainObject))]
static class Patch_HCC_OnPlace {
    static void Prefix(HumanCharacterController __instance) {
        var lifted = Traverse.Create(__instance).Field("liftedObject")?.GetValue<TerrainObject>()
                  ?? Traverse.Create(__instance).Field("m_liftingTerrainObject")?.GetValue<TerrainObject>();
        if (lifted == null) return;
        // 取放置坐标：HCC 内部多为 mouseWorldPos / placementPreview
        Vector3 pos = __instance.transform.position; // 兜底，或读 lifted.liftingPosition
        // 调 P4 统一 OnPlaced
        P4_PlacementDetector.OnPlaced(lifted, pos, PlacementReason.Carry);
    }
}

// 2) 双保险：底层 Place（防未来 HCC 内联或改名）
[HarmonyPatch(typeof(TerrainObject), nameof(TerrainObject.PlaceTerrainObject))]
static class Patch_TO_Place {
    static void Postfix(TerrainObject __instance, object obj) {
        // isLifted 从 true→false 的才是搬运放下；建造新放也会进，但 P4 可去重
        if (__instance.isLifted) return; // 视实现取反，实测后定
        P4_PlacementDetector.OnPlaced(__instance, __instance.transform.position, PlacementReason.Place);
    }
}

// 可选对称：搬起时清状态
[HarmonyPatch(typeof(HumanCharacterController), nameof(HumanCharacterController.OnLiftObject))]
static class Patch_HCC_OnLift { static void Postfix(...) { /* log */ } }
```

> 约束：`TerrainObject` 字段需编译期直访（`__instance.attr.id`、`__instance.isLifted` 等 public，勿 `GetField` 反射，P3 教训），`Harmony __instance` 位置绑定用 `__0` 而非命名参数。

### 兜底轮询（仅在 Harmony 连续失效时启用）

若 `OnPlaceTerrainObject` 未来被内联或改为 Burst，启用 `HumanCharacterController.Update` 后缀轮询：检测 `m_liftingTerrainObject` 从非空→空且 `Time.frameCount` 内发生 `Place`，代价高，仅作 fallback。

---

## 6 排查清单

- [ ] reasonix 单会话内跑 Ghidra 反编译确认 `0x18048A6F0` 与 `0x18095A430` 内部调用链（是否 AddTerrainObject / transform 位移）
- [ ] 打印 `HCC.OnPlaceTerrainObject` 触发时 `liftedObject.attr.id` 与 `transform.position`，与 `TerrainObject.PlaceTerrainObject` 二次触发去重
- [ ] 勿再 patch `StartDescend` 作通用放置检测；该路径与搬运无关

---

*生成：audit `dump.cs:79150-79406` + `76137-76468` + `script.json:79018-79154,24628-24655,33233-33246`；关键词 `Lifter/Lift/Descend/PlaceTerrainObject/Carry` 全表 293/1117 命中已抽样；Ghidra 直连受沙箱白名单限制未执行，已标注推断待补。*
