using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// 假飞可行性运行时探针 v0.14（内测探针，非发布物；发布前一律 .disabled）。
/// v0.13 实测结论（日志取证）：
///   ① quad sortingOrder=10000 保持生效（游戏不重置 quad 的 order），但车辆仍被树盖住
///      → SortingLayer 全表：Default(0) Surface(1) FX_BG(2) Character(3) FX_FG(4) Roof(5=最高)
///      → 树必然在更高层或同层 order 更大；对策：quad 飞行时切到最高层 Roof
///   ② 车灯：用户放弃转向跟随方案，车灯改回跟随车辆实体（游戏原生管理，探针零干预）
/// v0.14 变更：
///   ① quad 飞行时 sortingLayerName=Roof（游戏最高层，画在一切之上）+ order 保持；关闭恢复原层
///   ② 删除车灯全部干预（不挂 quad、不设位置/旋转）——车灯回归实体原生跟随
///   ③ tele quad 监控增打 layer 名
///   ④ 其余保留：quad y 拾升、F11 三态、z 悬浮、F10 提速、EnforcePhysicsSafety 拦截、软修
/// 键位：F9 开关 | F10 提速 | F11 碰撞三态 | F12 视觉(0/5) | F8/F7 高度 | F6 快照
/// </summary>
[BepInPlugin("com.zedzone.tool.flyprobe", "FlyProbe", "0.14.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;
    private Harmony _harmony;

    public override void Load()
    {
        Instance = this;
        L = Log;
        // 硬修：拦截物理异常回滚（v0.2 已验证有效，保留）
        try
        {
            _harmony = new Harmony("com.zedzone.tool.flyprobe");
            var target = AccessTools.Method(typeof(BasicVehicle), "EnforcePhysicsSafety");
            if (target != null)
            {
                var prefix = typeof(FlyProbePatch).GetMethod(nameof(FlyProbePatch.Prefix), BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log.LogInfo("[FlyProbe] 已挂钩 BasicVehicle.EnforcePhysicsSafety (Prefix 跳过)");
            }
            else
            {
                Log.LogWarning("[FlyProbe] 未找到 BasicVehicle.EnforcePhysicsSafety，硬修未生效");
            }
        }
        catch (Exception e)
        {
            Log.LogError($"[FlyProbe] Harmony 挂钩异常: {e}");
        }
        AddComponent<ProbeComponent>();
        Log.LogInfo("[FlyProbe] 假飞可行性探查已加载 (v0.14.0)");
    }
}

public static class FlyProbePatch
{
    public static bool Prefix()
    {
        if (ProbeComponent.IsFlying)
        {
            return false;
        }
        return true;
    }
}

public class ProbeComponent : MonoBehaviour
{
    private const float SpeedBoost = 5f;
    private const float SafeStepDistance = 100f;
    private const float VelBoostFactor = 1.25f;
    private const float VelBoostCap = 35f;
    private const int VisualSortBoost = 100;

    private static readonly string[] BoostNames = { "无(纯悬浮基线)", "maxLinearSpeed*5", "engine.maxTorque*2", "rb.velocity*1.25(cap35)" };
    private static readonly string[] VisualNames = { "无(z数据悬浮)", "standard", "standard", "standard", "standard", "标准悬浮(quad抬升+车灯)" };

    public static bool IsFlying => _isFlyingStatic;
    private static bool _isFlyingStatic;

    private bool _flying;
    private bool _colliderOff = false;   // colMode==2 时生效
    private int _colMode = 0;             // v0.12: 0=ON 1=智能穿墙 2=OFF
    private float _hoverOffset = 1f;   // v0.10: 相机扩视后 1m 不裁切
    private int _boostMode = 0;
    private int _visualMode = 5;   // v0.11: 默认标准悬浮模式
    private float _oldMaxSaneStep = 2.5f;
    private float _baseMaxLinearSpeed;
    private float _baseMaxTorque;
    private float _origQuadZ;
    private float _origHandlerZ;
    private float _origModelZ;
    private float _origQuadY;      // 模式5(v0.12)：quad 原世界 y（恢复用）
    private int _origQuadOrder;    // 模式5(v0.13)：quad 原 sortingOrder（恢复用）
    private string _origQuadLayer = "Character"; // 模式5(v0.14)：quad 原 sortingLayer 名（恢复用）
    private Quaternion _lightOffsetRotL;  // 车灯相对车辆朝向（开飞记录）
    private Quaternion _lightOffsetRotR;
    private Transform _origLightParentL;   // 车灯原父级
    private Transform _origLightParentR;
    private Vector3 _origLightLocalPosL;   // 车灯原 localPosition
    private Vector3 _origLightLocalPosR;
    private Quaternion _origLightLocalRotL; // 车灯原 localRotation
    private Quaternion _origLightLocalRotR;
    private float _telemetryTimer;

    // renderer 分组：
    //  orders —— 全部 renderer 原 sortingOrder（模式1 用）
    //  body   —— 节点名含 "Body"（独轮驱动外的车身主体，模式2/3/4 抬）
    //  other  —— 非 Tire 非 Body 非 Quad 的 model 树成员（模式4 抬）
    //  quad   —— VehicleQuad(Clone)（监控用，绝不抬）
    //  tires  —— 节点名含 "Tire"（监控用，绝不抬——游戏悬挂动画每帧重置）
    private readonly List<KeyValuePair<Renderer, int>> _orders = new List<KeyValuePair<Renderer, int>>();
    private readonly List<KeyValuePair<Transform, float>> _bodyPosY = new List<KeyValuePair<Transform, float>>();
    private readonly List<KeyValuePair<Transform, float>> _otherPosY = new List<KeyValuePair<Transform, float>>();
    private readonly List<KeyValuePair<Transform, float>> _quadPosY = new List<KeyValuePair<Transform, float>>();
    private readonly List<KeyValuePair<Transform, float>> _tirePosY = new List<KeyValuePair<Transform, float>>();

    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F9)) ToggleFly();
            if (Input.GetKeyDown(KeyCode.F8)) { _hoverOffset += 0.5f; Plugin.L.LogInfo($"[FlyProbe] 悬停高度 {_hoverOffset:F1}"); }
            if (Input.GetKeyDown(KeyCode.F7)) { _hoverOffset -= 0.5f; Plugin.L.LogInfo($"[FlyProbe] 悬停高度 {_hoverOffset:F1}"); }
            if (Input.GetKeyDown(KeyCode.F10)) CycleBoostMode();
            if (Input.GetKeyDown(KeyCode.F11)) ToggleCollider();
            if (Input.GetKeyDown(KeyCode.F12)) CycleVisualMode();
            if (Input.GetKeyDown(KeyCode.F6)) Snapshot();

            if (_flying)
            {
                ApplyFly();
                _telemetryTimer -= Time.deltaTime;
                if (_telemetryTimer <= 0f)
                {
                    _telemetryTimer = 2f;
                    Telemetry();
                }
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[FlyProbe] Update 异常: {e}");
        }
    }

    // 视觉（sorting/localY/z）在 LateUpdate 应用：游戏排序之后、渲染之前最后一刻覆盖
    private void LateUpdate()
    {
        if (!_flying) return;
        try
        {
            BasicVehicle v;
            if (CurrentVehicle(out v))
            {
                ApplyHoverZ(v);
                ApplyVisual(v);
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[FlyProbe] LateUpdate 异常: {e}");
        }
    }

    // ---------- 开关类 ----------

    private void ToggleFly()
    {
        if (_flying)
        {
            Restore();
            _flying = false;
            _isFlyingStatic = false;
            Plugin.L.LogInfo("[FlyProbe] 飞行模式 已关闭（已恢复 collider/速度/视觉z/sortingOrder/渲染偏移）");
            return;
        }

        BasicVehicle v;
        if (!CurrentVehicle(out v))
        {
            Plugin.L.LogInfo("[FlyProbe] 当前不在驾驶载具，无法开启飞行模式");
            return;
        }

        _oldMaxSaneStep = ReadMaxSaneStep(v);
        _baseMaxLinearSpeed = ReadFloatProp(v, "maxLinearSpeed");
        _baseMaxTorque = ReadFloatProp(v.engine, "maxTorque");
        try { _origQuadZ = v.vehicleQuadObj != null ? v.vehicleQuadObj.transform.position.z : 0f; } catch { _origQuadZ = 0f; }
        try { _origHandlerZ = v.vehicleCollisionHandler != null ? v.vehicleCollisionHandler.transform.position.z : 0f; } catch { _origHandlerZ = 0f; }
        try { _origModelZ = v.vehicleModel != null ? v.vehicleModel.transform.position.z : 0f; } catch { _origModelZ = 0f; }
        try { _origQuadY = v.vehicleQuadObj != null ? v.vehicleQuadObj.transform.position.y : 0f; } catch { _origQuadY = 0f; }
        try
        {
            _origQuadOrder = 0;
            if (v.vehicleQuadObj != null)
            {
                var qmr = v.vehicleQuadObj.GetComponent<UnityEngine.MeshRenderer>();
                var qsr = v.vehicleQuadObj.GetComponent<UnityEngine.SpriteRenderer>();
                if (qmr != null) { _origQuadOrder = qmr.sortingOrder; _origQuadLayer = qmr.sortingLayerName; }
                if (qsr != null) { _origQuadOrder = qsr.sortingOrder; _origQuadLayer = qsr.sortingLayerName; }
            }
        }
        catch { _origQuadOrder = 0; _origQuadLayer = "Character"; }
        try { _lightOffsetRotL = (v.headlight_Left != null && v.transform != null) ? Quaternion.Inverse(v.transform.rotation) * v.headlight_Left.transform.rotation : Quaternion.identity; } catch { _lightOffsetRotL = Quaternion.identity; }
        try { _lightOffsetRotR = (v.headlight_Right != null && v.transform != null) ? Quaternion.Inverse(v.transform.rotation) * v.headlight_Right.transform.rotation : Quaternion.identity; } catch { _lightOffsetRotR = Quaternion.identity; }
        try { _origLightParentL = v.headlight_Left != null ? v.headlight_Left.transform.parent : null; } catch { _origLightParentL = null; }
        try { _origLightParentR = v.headlight_Right != null ? v.headlight_Right.transform.parent : null; } catch { _origLightParentR = null; }
        try { _origLightLocalPosL = v.headlight_Left != null ? v.headlight_Left.transform.localPosition : Vector3.zero; } catch { _origLightLocalPosL = Vector3.zero; }
        try { _origLightLocalPosR = v.headlight_Right != null ? v.headlight_Right.transform.localPosition : Vector3.zero; } catch { _origLightLocalPosR = Vector3.zero; }
        try { _origLightLocalRotL = v.headlight_Left != null ? v.headlight_Left.transform.localRotation : Quaternion.identity; } catch { _origLightLocalRotL = Quaternion.identity; }
        try { _origLightLocalRotR = v.headlight_Right != null ? v.headlight_Right.transform.localRotation : Quaternion.identity; } catch { _origLightLocalRotR = Quaternion.identity; }
        CollectRenderers(v);
        _flying = true;
        _isFlyingStatic = true;
        Plugin.L.LogInfo($"[FlyProbe] 飞行模式 已开启：悬停高度={_hoverOffset:F1} collider={ColModeName()} " +
                         $"提速={_boostMode}({BoostNames[_boostMode]}) 视觉={_visualMode}({VisualNames[_visualMode]}) " +
                         $"原z: quad={_origQuadZ} handler={_origHandlerZ} model={_origModelZ} " +
                         $"renderer: 总={_orders.Count} body={_bodyPosY.Count} tire={_tirePosY.Count} quad={_quadPosY.Count} other={_otherPosY.Count}");
        ApplyFly();
    }

    private void Restore()
    {
        BasicVehicle v;
        if (!CurrentVehicle(out v)) return;
        try
        {
            var rf = v.m_rigidbody;
            if (rf != null) { rf.isKinematic = false; rf.gravityScale = 0f; }
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复刚体异常: {e.Message}"); }
        try
        {
            var cf = v.vehicleCollider2D;
            if (cf != null) cf.enabled = true;
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复碰撞异常: {e.Message}"); }
        try
        {
            bool ok = SetProp(v, "maxSaneStepDistance", _oldMaxSaneStep);
            Plugin.L.LogInfo($"[FlyProbe] 恢复 maxSaneStepDistance={_oldMaxSaneStep} ok={ok}");
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复 maxSaneStep 异常: {e.Message}"); }
        if (_boostMode == 1)
        {
            try
            {
                bool ok = SetProp(v, "maxLinearSpeed", _baseMaxLinearSpeed);
                Plugin.L.LogInfo($"[FlyProbe] 恢复 maxLinearSpeed={_baseMaxLinearSpeed} ok={ok}");
            }
            catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复 maxLinearSpeed 异常: {e.Message}"); }
        }
        if (_boostMode == 2)
        {
            try
            {
                bool ok = SetProp(v.engine, "maxTorque", _baseMaxTorque);
                Plugin.L.LogInfo($"[FlyProbe] 恢复 engine.maxTorque={_baseMaxTorque} ok={ok}");
            }
            catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复 maxTorque 异常: {e.Message}"); }
        }
        // 视觉恢复
        RestoreVisual();
        // z 恢复
        try
        {
            var quad = v.vehicleQuadObj;
            if (quad != null) { var qp = quad.transform.position; quad.transform.position = new Vector3(qp.x, qp.y, _origQuadZ); }
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复 quad z 异常: {e.Message}"); }
        try
        {
            var handler = v.vehicleCollisionHandler;
            if (handler != null) { var hp = handler.transform.position; handler.transform.position = new Vector3(hp.x, hp.y, _origHandlerZ); }
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复 handler z 异常: {e.Message}"); }
        try
        {
            var model = v.vehicleModel;
            if (model != null) { var mp = model.transform.position; model.transform.position = new Vector3(mp.x, mp.y, _origModelZ); }
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 恢复 model z 异常: {e.Message}"); }
    }

    private void ToggleCollider()
    {
        // v0.12：三态循环 ON → 智能穿墙 → OFF
        _colliderOff = false;
        _colMode = (_colMode + 1) % 3;
        switch (_colMode)
        {
            case 0: Plugin.L.LogInfo("[FlyProbe] 碰撞 -> ON(全碰撞保摩擦)"); break;
            case 1: Plugin.L.LogInfo("[FlyProbe] 碰撞 -> 智能穿墙(excludeLayers=Building|SurfaceObject|Doors)"); break;
            case 2: Plugin.L.LogInfo("[FlyProbe] 碰撞 -> OFF(全关=滑冰对照)"); _colliderOff = true; break;
        }
        // 立即应用一次
        BasicVehicle v;
        if (CurrentVehicle(out v)) ApplyColliderMode(v);
    }

    private void CycleBoostMode()
    {
        _boostMode = (_boostMode + 1) % BoostNames.Length;
        Plugin.L.LogInfo($"[FlyProbe] 提速方案 -> {_boostMode}: {BoostNames[_boostMode]}");
        if (_flying)
        {
            BasicVehicle v;
            if (CurrentVehicle(out v)) ApplyBoost(v);
        }
    }

    private void CycleVisualMode()
    {
        // v0.11：精简为 0/5 二态（5 = 标准悬浮主方案）
        _visualMode = (_visualMode == 0) ? 5 : 0;
        Plugin.L.LogInfo($"[FlyProbe] 视觉模式 -> {_visualMode}: {VisualNames[_visualMode]}");
        if (_flying)
        {
            RestoreVisual();
            BasicVehicle v;
            if (CurrentVehicle(out v)) { ApplyHoverZ(v); ApplyVisual(v); }
        }
    }

    // ---------- 渲染对象收集（按名分类） ----------

    private void CollectRenderers(BasicVehicle v)
    {
        _orders.Clear();
        _bodyPosY.Clear();
        _otherPosY.Clear();
        _quadPosY.Clear();
        _tirePosY.Clear();
        try
        {
            // model 树
            if (v.vehicleModel != null)
            {
                CollectRenderersRecursive(v.vehicleModel.transform, false);
            }
            // quad（独立板，只监控不抬）
            if (v.vehicleQuadObj != null && v.vehicleQuadObj != v.vehicleModel)
            {
                var mr = v.vehicleQuadObj.GetComponent<UnityEngine.MeshRenderer>();
                if (mr != null) { _orders.Add(new KeyValuePair<Renderer, int>(mr, mr.sortingOrder)); _quadPosY.Add(new KeyValuePair<Transform, float>(mr.transform, mr.transform.localPosition.y)); }
                var sr = v.vehicleQuadObj.GetComponent<UnityEngine.SpriteRenderer>();
                if (sr != null) { _orders.Add(new KeyValuePair<Renderer, int>(sr, sr.sortingOrder)); _quadPosY.Add(new KeyValuePair<Transform, float>(sr.transform, sr.transform.localPosition.y)); }
            }
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 收集 renderer 异常: {e.Message}"); }
    }

    private void CollectRenderersRecursive(Transform t, bool isQuadBranch)
    {
        if (t == null) return;
        var sr = t.GetComponent<UnityEngine.SpriteRenderer>();
        var mr = t.GetComponent<UnityEngine.MeshRenderer>();
        string n = t.name ?? "";
        bool hasRenderer = sr != null || mr != null;
        if (hasRenderer)
        {
            if (sr != null) _orders.Add(new KeyValuePair<Renderer, int>(sr, sr.sortingOrder));
            if (mr != null) _orders.Add(new KeyValuePair<Renderer, int>(mr, mr.sortingOrder));
            float y = t.localPosition.y;
            if (isQuadBranch)
            {
                _quadPosY.Add(new KeyValuePair<Transform, float>(t, y));
            }
            else if (n.Contains("Tire") || n.Contains("tire"))
            {
                _tirePosY.Add(new KeyValuePair<Transform, float>(t, y));
            }
            else if (n.Contains("Body") || n.Contains("body"))
            {
                _bodyPosY.Add(new KeyValuePair<Transform, float>(t, y));
            }
            else
            {
                _otherPosY.Add(new KeyValuePair<Transform, float>(t, y));
            }
        }
        for (int i = 0; i < t.childCount; i++)
        {
            CollectRenderersRecursive(t.GetChild(i), isQuadBranch);
        }
    }

    // ---------- 视觉应用（LateUpdate 调用） ----------

    private void ApplyVisual(BasicVehicle v)
    {
        // 模式1/3：sortingOrder 抬升（对照组）
        if (_visualMode == 1 || _visualMode == 3)
        {
            foreach (var kv in _orders)
            {
                try { kv.Key.sortingOrder = kv.Value + VisualSortBoost; }
                catch (Exception e) { Plugin.L.LogError($"[FlyProbe] sortingOrder 设置异常: {e.Message}"); }
            }
        }
        // 模式2/3/4：localY 绝对偏移（乌鸦式主体上移；轮胎/quad 绝不抬）
        if (_visualMode == 2 || _visualMode == 3 || _visualMode == 4)
        {
            foreach (var kv in _bodyPosY)
            {
                try
                {
                    var p = kv.Key.localPosition;
                    kv.Key.localPosition = new Vector3(p.x, kv.Value + _hoverOffset, p.z);
                }
                catch (Exception e) { Plugin.L.LogError($"[FlyProbe] body localY 偏移异常: {e.Message}"); }
            }
            if (_visualMode == 4)
            {
                foreach (var kv in _otherPosY)
                {
                    try
                    {
                        var p = kv.Key.localPosition;
                        kv.Key.localPosition = new Vector3(p.x, kv.Value + _hoverOffset, p.z);
                    }
                    catch (Exception e) { Plugin.L.LogError($"[FlyProbe] other localY 偏移异常: {e.Message}"); }
                }
            }
        }
        // 模式5（v0.12 定稿）：仅动态抬 quad 世界 y（视觉车位置）；model/camera 照片不动
        if (_visualMode == 5)
        {
            // ① quad 世界 y 动态抬升（x/z 由游戏同步）+ 切最高层 Roof + order 10000（防树/建筑盖住）
            try
            {
                if (v.vehicleQuadObj != null && v.transform != null)
                {
                    var qp = v.vehicleQuadObj.transform.position;
                    v.vehicleQuadObj.transform.position = new Vector3(qp.x, v.transform.position.y + _hoverOffset, qp.z);
                    var qmr = v.vehicleQuadObj.GetComponent<UnityEngine.MeshRenderer>();
                    var qsr = v.vehicleQuadObj.GetComponent<UnityEngine.SpriteRenderer>();
                    if (qmr != null) { qmr.sortingOrder = 10000; qmr.sortingLayerName = "Roof"; }
                    if (qsr != null) { qsr.sortingOrder = 10000; qsr.sortingLayerName = "Roof"; }
                }
            }
            catch (Exception e) { Plugin.L.LogError($"[FlyProbe] quad 抬升异常: {e.Message}"); }
            // ② 车灯：v0.14 起零干预（跟随车辆实体原生逻辑）
        }

        // 模式6/7：抬 VehicleQuad 板（世界视觉本体假设）
        if (_visualMode == 6 || _visualMode == 7)
        {
            foreach (var kv in _quadPosY)
            {
                try
                {
                    var p = kv.Key.localPosition;
                    kv.Key.localPosition = new Vector3(p.x, kv.Value + _hoverOffset, p.z);
                }
                catch (Exception e) { Plugin.L.LogError($"[FlyProbe] quad 板偏移异常: {e.Message}"); }
            }
            // 模式7 = 6 + Body
            if (_visualMode == 7)
            {
                foreach (var kv in _bodyPosY)
                {
                    try
                    {
                        var p = kv.Key.localPosition;
                        kv.Key.localPosition = new Vector3(p.x, kv.Value + _hoverOffset, p.z);
                    }
                    catch (Exception e) { Plugin.L.LogError($"[FlyProbe] body localY 偏移异常: {e.Message}"); }
                }
            }
        }
    }

    private void RestoreVisual()
    {
        foreach (var kv in _orders)
        {
            try { kv.Key.sortingOrder = kv.Value; }
            catch { }
        }
        foreach (var list in new[] { _bodyPosY, _otherPosY, _quadPosY, _tirePosY })
        {
            foreach (var kv in list)
            {
                try
                {
                    var p = kv.Key.localPosition;
                    kv.Key.localPosition = new Vector3(p.x, kv.Value, p.z);
                }
                catch { }
            }
        }
        // v0.12：quad y 还原 + 车灯脱离还原 + excludeLayers 清零
        try
        {
            BasicVehicle v;
            if (CurrentVehicle(out v))
            {
                if (v.vehicleQuadObj != null)
                {
                    var qp = v.vehicleQuadObj.transform.position;
                    v.vehicleQuadObj.transform.position = new Vector3(qp.x, _origQuadY, qp.z);
                    var qmr = v.vehicleQuadObj.GetComponent<UnityEngine.MeshRenderer>();
                    var qsr = v.vehicleQuadObj.GetComponent<UnityEngine.SpriteRenderer>();
                    if (qmr != null) { qmr.sortingOrder = _origQuadOrder; qmr.sortingLayerName = _origQuadLayer; }
                    if (qsr != null) { qsr.sortingOrder = _origQuadOrder; qsr.sortingLayerName = _origQuadLayer; }
                }
                if (v.headlight_Left != null)
                {
                    if (_origLightParentL != null) v.headlight_Left.transform.SetParent(_origLightParentL, false);
                    v.headlight_Left.transform.localPosition = _origLightLocalPosL;
                    v.headlight_Left.transform.localRotation = _origLightLocalRotL;
                }
                if (v.headlight_Right != null)
                {
                    if (_origLightParentR != null) v.headlight_Right.transform.SetParent(_origLightParentR, false);
                    v.headlight_Right.transform.localPosition = _origLightLocalPosR;
                    v.headlight_Right.transform.localRotation = _origLightLocalRotR;
                }
                if (v.vehicleCollider2D != null)
                {
                    v.vehicleCollider2D.enabled = true;
                    try { v.vehicleCollider2D.excludeLayers = (LayerMask)0; } catch { }
                }
            }
        }
        catch { }
    }

    // ---------- 每帧执行（Update：物理/数据层） ----------

    private void ApplyFly()
    {
        BasicVehicle v;
        if (!CurrentVehicle(out v))
        {
            _flying = false;
            _isFlyingStatic = false;
            Plugin.L.LogInfo("[FlyProbe] 已离开载具，飞行模式自动关闭");
            return;
        }

        var rf = v.m_rigidbody;
        var cf = v.vehicleCollider2D;

        try { ApplyColliderMode(v); }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 设置碰撞异常: {e.Message}"); }

        try
        {
            if (rf != null)
            {
                rf.isKinematic = false;
                rf.gravityScale = 0f;
            }
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 设置刚体异常: {e.Message}"); }

        try { ApplyBoost(v); } catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 提速异常: {e.Message}"); }

        try { SetProp(v, "lastSanePosition", v.transform.position); } catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 同步 lastSanePosition 异常: {e.Message}"); }
        try { SetProp(v, "maxSaneStepDistance", SafeStepDistance); } catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 设置 maxSaneStepDistance 异常: {e.Message}"); }
    }

    /// <summary>z 数据悬浮（LateUpdate 保持）</summary>
    private void ApplyHoverZ(BasicVehicle v)
    {
        try
        {
            Transform t = v.transform;
            Vector3 p = t.position;
            float ground = GetGroundHeight(new Vector2(p.x, p.y));
            float targetZ = ground + _hoverOffset;
            t.position = new Vector3(p.x, p.y, targetZ);
            var quad = v.vehicleQuadObj;
            if (quad != null) { var qp = quad.transform.position; quad.transform.position = new Vector3(qp.x, qp.y, targetZ); }
            // v0.12：不再写 model/handler 的 z（model z 被游戏 chunk 管控，写必冲突）
        }
        catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 悬停写 z 异常: {e.Message}"); }
    }

    private void ApplyColliderMode(BasicVehicle v)
    {
        // v0.12 智能穿墙：保持 collider 开启（保留地面摩擦），只排除障碍层
        var cf = v.vehicleCollider2D;
        if (cf == null) return;
        switch (_colMode)
        {
            case 0:
                cf.enabled = true;
                try { cf.excludeLayers = (LayerMask)0; } catch { }
                break;
            case 1:
                cf.enabled = true;
                // 障碍层：15=SurfaceObject(树/矿石) 16=Building 22=Doors
                try { cf.excludeLayers = (LayerMask)((1 << 15) | (1 << 16) | (1 << 22)); }
                catch (Exception e) { Plugin.L.LogError($"[FlyProbe] 智能穿墙 excludeLayers 失败: {e.Message}"); }
                break;
            case 2:
                cf.enabled = false;
                break;
        }
    }

    private void ApplyBoost(BasicVehicle v)
    {
        switch (_boostMode)
        {
            case 1:
                SetProp(v, "maxLinearSpeed", _baseMaxLinearSpeed * SpeedBoost);
                break;
            case 2:
                if (v.engine != null) SetProp(v.engine, "maxTorque", _baseMaxTorque * 2f);
                break;
            case 3:
                var rf = v.m_rigidbody;
                if (rf != null && rf.velocity.magnitude > 0.01f)
                {
                    Vector2 nv = rf.velocity * VelBoostFactor;
                    if (nv.magnitude > VelBoostCap)
                        nv = nv.normalized * VelBoostCap;
                    rf.velocity = nv;
                }
                break;
            default:
                break;
        }
    }

    // ---------- 遥测 / 快照 ----------

    private void Telemetry()
    {
        BasicVehicle v;
        if (!CurrentVehicle(out v)) return;
        Vector3 p = v.transform.position;
        var rf = v.m_rigidbody;
        Vector2 rbPos = rf != null ? rf.position : new Vector2(p.x, p.y);
        float ground = GetGroundHeight(rbPos);
        Vector3 quadPos = v.vehicleQuadObj != null ? v.vehicleQuadObj.transform.position : Vector3.zero;
        Vector3 handlerPos = v.vehicleCollisionHandler != null ? v.vehicleCollisionHandler.transform.position : Vector3.zero;

        // 监控：body 原→当前；首个 tire 当前 y
        string bodyInfo = "无body";
        if (_bodyPosY.Count > 0)
        {
            var t = _bodyPosY[0].Key;
            bodyInfo = $"{t.name}: {_bodyPosY[0].Value:F2}->{t.localPosition.y:F2}";
        }
        string tireInfo = "无tire";
        if (_tirePosY.Count > 0)
        {
            var t = _tirePosY[0].Key;
            tireInfo = $"{t.name}: 原{_tirePosY[0].Value:F2} 现{t.localPosition.y:F2}";
        }
        string sortInfo = "无renderer";
        if (_orders.Count > 0)
        {
            int cur = -999;
            try { cur = _orders[0].Key.sortingOrder; } catch { }
            sortInfo = $"{_orders[0].Key.name}: {_orders[0].Value}->{cur}";
        }
        // v0.11：相机 y 与车灯监控
        string camInfo = "无相机";
        if (v.vehicleCamera != null) camInfo = $"camY {v.vehicleCamera.transform.position.y:F2}";
        string quadInfo = "无quad";
        if (v.vehicleQuadObj != null)
        {
            var qmr = v.vehicleQuadObj.GetComponent<UnityEngine.MeshRenderer>();
            var qsr = v.vehicleQuadObj.GetComponent<UnityEngine.SpriteRenderer>();
            int ord = (qmr != null) ? qmr.sortingOrder : ((qsr != null) ? qsr.sortingOrder : -999);
            string lyr = (qmr != null) ? qmr.sortingLayerName : ((qsr != null) ? qsr.sortingLayerName : "?");
            quadInfo = $"y={v.vehicleQuadObj.transform.position.y:F2} order={ord} layer={lyr}";
        }
        string lightInfo = "无车灯";
        if (v.headlight_Left != null) lightInfo = $"L灯y={v.headlight_Left.transform.position.y:F2} 父级={(v.headlight_Left.transform.parent != null ? v.headlight_Left.transform.parent.name : "null")}";
        Plugin.L.LogInfo(
            $"[FlyProbe] tele pos={F(p)} rb=({rbPos.x:F2},{rbPos.y:F2}) quad={F(quadPos)} handler={F(handlerPos)} ground={ground:F2} 期望z={ground + _hoverOffset:F2} | " +
            $"rbVel={(rf != null ? rf.velocity.ToString() : "-")} real={GetProp(v, "realVelocity")} rigid={GetProp(v, "rigidVelocity")} spd={GetProp(v, "speedFloat")} | " +
            $"col={ColModeName()} boost={_boostMode} 视觉={_visualMode}({VisualNames[_visualMode]}) {bodyInfo} tire[{tireInfo}] sort[{sortInfo}] {camInfo} {lightInfo} quad[{quadInfo}]");
    }

    private void Snapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[FlyProbe] ===== F6 快照 (flying={_flying}, offset={_hoverOffset:F1}, col={(_colliderOff ? "OFF" : "ON")}, boost={_boostMode}, 视觉={_visualMode}:{VisualNames[_visualMode]}) =====");
        var gc = GameController.instance;
        sb.AppendLine($"  GameController.instance = {(gc != null ? "OK" : "NULL")}");
        if (gc != null)
        {
            var pc = gc.playerCharacter;
            sb.AppendLine($"  player = {(pc != null ? pc.name : "NULL")} | isDriving={GetProp(pc, "isDriving")}");
            if (pc != null)
            {
                var v = GetProp(pc, "drivingVehicle") as BasicVehicle;
                sb.AppendLine($"  drivingVehicle = {(v != null ? v.name : "NULL")}");
                if (v != null)
                {
                    try { sb.AppendLine($"  transform.position = {F(v.transform.position)}"); }
                    catch (Exception e) { sb.AppendLine($"  position 读取异常: {e.Message}"); }
                    sb.AppendLine($"  realVelocity = {GetProp(v, "realVelocity")} | rigidVelocity = {GetProp(v, "rigidVelocity")} | lastSanePosition = {GetProp(v, "lastSanePosition")}");
                    sb.AppendLine($"  isPhysicsAnomalous = {GetProp(v, "isPhysicsAnomalous")} | maxSaneStepDistance = {GetProp(v, "maxSaneStepDistance")}");
                    sb.AppendLine($"  maxLinearSpeed = {ReadFloatProp(v, "maxLinearSpeed")}");
                    try
                    {
                        var rf = v.m_rigidbody;
                        if (rf != null) sb.AppendLine($"  rb: velocity={rf.velocity} position={rf.position} isKinematic={rf.isKinematic} gravityScale={rf.gravityScale} simulated={rf.simulated}");
                        else sb.AppendLine("  rb: NULL");
                        var cf = v.vehicleCollider2D;
                        if (cf != null) sb.AppendLine($"  col: enabled={cf.enabled} isTrigger={cf.isTrigger} bounds={cf.bounds}");
                        else sb.AppendLine("  col: NULL");
                        var quad = v.vehicleQuadObj;
                        if (quad != null) sb.AppendLine($"  quad localPos={F(quad.transform.localPosition)} worldPos={F(quad.transform.position)}");
                        var handler = v.vehicleCollisionHandler;
                        if (handler != null) sb.AppendLine($"  handler worldPos={F(handler.transform.position)}");
                        var model = v.vehicleModel;
                        if (model != null) sb.AppendLine($"  model localPos={F(model.transform.localPosition)} worldPos={F(model.transform.position)}");
                        var engine = v.engine;
                        if (engine != null) sb.AppendLine($"  engine: maxTorque={ReadFloatProp(engine, "maxTorque")} rpm={GetProp(engine, "rpm")} maxRpm={GetProp(engine, "maxRpm")} gearIndex={GetProp(engine, "gearIndex")}");
                    }
                    catch (Exception e) { sb.AppendLine($"  刚体/碰撞/视觉读取异常: {e.Message}"); }
                    Vector2 rbPos = v.m_rigidbody != null ? v.m_rigidbody.position : new Vector2(v.transform.position.x, v.transform.position.y);
                    sb.AppendLine($"  groundHeight(rb) = {GetGroundHeight(rbPos)}");
                    sb.AppendLine("  ==== renderer 分组（v0.8 分类）====");
                    sb.AppendLine($"  body组({_bodyPosY.Count}): {string.Join(", ", _bodyPosY.ConvertAll(kv => $"{kv.Key.name}={kv.Value:F2}->{kv.Key.localPosition.y:F2}").ToArray())}");
                    sb.AppendLine($"  tire组({_tirePosY.Count}): {string.Join(", ", _tirePosY.ConvertAll(kv => $"{kv.Key.name}({kv.Value:F2}->{kv.Key.localPosition.y:F2})").ToArray())}");
                    sb.AppendLine($"  quad组({_quadPosY.Count}): {string.Join(", ", _quadPosY.ConvertAll(kv => $"{kv.Key.name}({kv.Value:F2}->{kv.Key.localPosition.y:F2})").ToArray())}");
                    sb.AppendLine($"  other组({_otherPosY.Count}): {string.Join(", ", _otherPosY.ConvertAll(kv => $"{kv.Key.name}({kv.Value:F2}->{kv.Key.localPosition.y:F2})").ToArray())}");
                    // 图层取证（v0.11）
                    sb.AppendLine("  ==== 图层取证（v0.11）====");
                    try
                    {
                        var cf = v.vehicleCollider2D;
                        if (cf != null)
                        {
                            sb.AppendLine($"  vehicleCollider2D layer={cf.gameObject.layer}({UnityEngine.LayerMask.LayerToName(cf.gameObject.layer)})");
                            try { sb.AppendLine($"  collider.excludeLayers 可读={cf.excludeLayers}"); } catch (Exception ex2) { sb.AppendLine($"  collider.excludeLayers 不可用: {ex2.Message}"); }
                        }
                        var rb2 = v.m_rigidbody;
                        if (rb2 != null)
                        {
                            try { sb.AppendLine($"  rigidbody.excludeLayers 可读={rb2.excludeLayers}"); } catch (Exception ex3) { sb.AppendLine($"  rigidbody.excludeLayers 不可用: {ex3.Message}"); }
                        }
                        // 周边 6m collider 层分布
                        Vector2 center = rb2 != null ? rb2.position : new Vector2(v.transform.position.x, v.transform.position.y);
                        var nearby = Physics2D.OverlapCircleAll(center, 6f);
                        var layerCount = new System.Collections.Generic.SortedDictionary<int, int>();
                        var layerName = new System.Collections.Generic.Dictionary<int, string>();
                        foreach (var c in nearby)
                        {
                            int l = c.gameObject.layer;
                            if (!layerCount.ContainsKey(l)) { layerCount[l] = 0; layerName[l] = UnityEngine.LayerMask.LayerToName(l); }
                            layerCount[l]++;
                        }
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var kv in layerCount) parts.Add($"{kv.Key}:{layerName[kv.Key]}x{kv.Value}");
                        sb.AppendLine($"  周边collider {nearby.Length} 个: {string.Join(", ", parts.ToArray())}");
                        // 全部 32 层清单
                        var all = new System.Collections.Generic.List<string>();
                        for (int li = 0; li < 32; li++)
                        {
                            string nm = UnityEngine.LayerMask.LayerToName(li);
                            if (!string.IsNullOrEmpty(nm)) all.Add($"{li}={nm}");
                        }
                        sb.AppendLine($"  命名层清单: {string.Join(", ", all.ToArray())}");
                    }
                    catch (Exception e2) { sb.AppendLine($"  图层取证异常: {e2.Message}"); }
                    // v0.13：SortingLayer 全表 + quad 排序
                    sb.AppendLine("  ==== SortingLayer 诊断（v0.13）====");
                    try
                    {
                        var layers = UnityEngine.SortingLayer.layers;
                        var slParts = new System.Collections.Generic.List<string>();
                        foreach (var sl in layers)
                            slParts.Add($"{sl.name}(id={sl.id},v={sl.value})");
                        sb.AppendLine($"  SortingLayer 全表: {string.Join(", ", slParts.ToArray())}");
                        if (v.vehicleQuadObj != null)
                        {
                            var qmr = v.vehicleQuadObj.GetComponent<UnityEngine.MeshRenderer>();
                            var qsr = v.vehicleQuadObj.GetComponent<UnityEngine.SpriteRenderer>();
                            if (qmr != null) sb.AppendLine($"  quad.MeshRenderer sortingLayer={qmr.sortingLayerName} order={qmr.sortingOrder}");
                            if (qsr != null) sb.AppendLine($"  quad.SpriteRenderer sortingLayer={qsr.sortingLayerName} order={qsr.sortingOrder}");
                        }
                    }
                    catch (Exception e3) { sb.AppendLine($"  SortingLayer 诊断异常: {e3.Message}"); }
                    // 渲染链诊断（v0.9）：vehicleCamera / vehicleRenderTexture / quad 纹理
                    sb.AppendLine("  ==== 渲染链诊断（v0.9）====");
                    try
                    {
                        var cam = v.vehicleCamera;
                        if (cam != null)
                        {
                            sb.AppendLine($"  vehicleCamera: pos={F(cam.transform.position)} orthographic={cam.orthographic} size={(cam.orthographic ? cam.orthographicSize.ToString() : "-")} targetTex={(cam.targetTexture != null ? cam.targetTexture.name : "null")}");
                        }
                        else sb.AppendLine("  vehicleCamera: NULL");
                        if (v.vehicleRenderTexture != null) sb.AppendLine($"  vehicleRenderTexture: {v.vehicleRenderTexture.name} {v.vehicleRenderTexture.width}x{v.vehicleRenderTexture.height}");
                        else sb.AppendLine("  vehicleRenderTexture: NULL");
                        if (v.vehicleQuadObj != null)
                        {
                            var qmr = v.vehicleQuadObj.GetComponent<UnityEngine.MeshRenderer>();
                            if (qmr != null)
                            {
                                bool isRt = v.vehicleRenderTexture != null && qmr.material != null && qmr.material.mainTexture == v.vehicleRenderTexture;
                                sb.AppendLine($"  VehicleQuad.MeshRenderer: material={(qmr.material != null ? qmr.material.name : "null")} mainTex={(qmr.material != null && qmr.material.mainTexture != null ? qmr.material.mainTexture.name : "null")} 是RenderTexture板={isRt}");
                            }
                        }
                        if (v.vehicleMeshRenderer != null) sb.AppendLine($"  vehicleMeshRenderer: {v.vehicleMeshRenderer.name} order={v.vehicleMeshRenderer.sortingOrder}");
                    }
                    catch (Exception e) { sb.AppendLine($"  渲染链诊断异常: {e.Message}"); }
                }
            }
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // ---------- 辅助 ----------

    private static bool CurrentVehicle(out BasicVehicle v)
    {
        v = null;
        try
        {
            var gc = GameController.instance;
            if (gc == null) return false;
            var pc = gc.playerCharacter;
            if (pc == null) return false;
            v = GetProp(pc, "drivingVehicle") as BasicVehicle;
            return v != null;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[FlyProbe] CurrentVehicle 异常: {e.Message}");
            return false;
        }
    }

    private static float GetGroundHeight(Vector2 pos)
    {
        try
        {
            var mc = MapController.instance;
            if (mc == null) return 0f;
            return mc.GetTerrainTempHeightByWorldPosition(new Vector2(pos.x, pos.y));
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[FlyProbe] GetGroundHeight 异常: {e.Message}");
            return 0f;
        }
    }

    private static float ReadFloatProp(object obj, string name)
    {
        try
        {
            var o = GetProp(obj, name);
            if (o == null) return 0f;
            if (o is float f) return f;
            return Convert.ToSingle(o);
        }
        catch { return 0f; }
    }

    private static float ReadMaxSaneStep(object v)
    {
        try
        {
            var o = GetProp(v, "maxSaneStepDistance");
            if (o is float f) return f;
            if (o != null) return Convert.ToSingle(o);
            return 2.5f;
        }
        catch { return 2.5f; }
    }

    private static string F(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    private string ColModeName()
    {
        switch (_colMode)
        {
            case 1: return "穿墙";
            case 2: return "OFF(滑)";
            default: return "ON";
        }
    }

    // 三级反射：字段 → 属性 → get_/set_ 方法，与 Shared/Reflect.cs 同逻辑
    private static object GetProp(object obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) return f.GetValue(obj);
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null)
        {
            try { return p.GetValue(obj); } catch { }
        }
        var getter = t.GetMethod("get_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (getter != null)
        {
            try { return getter.Invoke(obj, null); } catch { }
        }
        return null;
    }

    private static bool SetProp(object obj, string name, object value)
    {
        if (obj == null) { Plugin.L.LogWarning($"[FlyProbe] 成员不可写: (obj null).{name}"); return false; }
        var t = obj.GetType();
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null)
        {
            try { f.SetValue(obj, ConvertValue(value, f.FieldType)); return true; }
            catch (Exception e) { Plugin.L.LogError($"[FlyProbe] SetField {t.Name}.{name} 失败: {e.Message}"); return false; }
        }
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null && p.CanWrite)
        {
            try { p.SetValue(obj, ConvertValue(value, p.PropertyType)); return true; }
            catch (Exception e) { Plugin.L.LogError($"[FlyProbe] SetProp {t.Name}.{name} 失败: {e.Message}"); return false; }
        }
        var setter = t.GetMethod("set_" + name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (setter != null)
        {
            try
            {
                var pt = setter.GetParameters()[0].ParameterType;
                setter.Invoke(obj, new[] { ConvertValue(value, pt) });
                return true;
            }
            catch (Exception e) { Plugin.L.LogError($"[FlyProbe] set_{t.Name}.{name} 失败: {e.Message}"); return false; }
        }
        Plugin.L.LogWarning($"[FlyProbe] 成员不可写: {t.Name}.{name}");
        return false;
    }

    private static object ConvertValue(object value, Type target)
    {
        if (value == null) return null;
        if (target.IsInstanceOfType(value)) return value;
        if (target.IsEnum) return Enum.ToObject(target, value);
        if (target == typeof(float)) return System.Convert.ToSingle(value);
        if (target == typeof(int)) return System.Convert.ToInt32(value);
        if (target == typeof(double)) return System.Convert.ToDouble(value);
        if (target == typeof(bool)) return System.Convert.ToBoolean(value);
        if (target == typeof(long)) return System.Convert.ToInt64(value);
        if (target == typeof(string)) return System.Convert.ToString(value);
        if (target == typeof(Vector3) && value is Vector3) return value;
        if (target == typeof(Vector2) && value is Vector2) return value;
        return value;
    }
}