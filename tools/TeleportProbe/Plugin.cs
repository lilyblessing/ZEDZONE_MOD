using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using ZedZoneShared;

/// <summary>
/// 传送可行性运行时探针 v0.13.0（内测探针，非发布物；发布前一律 .disabled）。
/// 键位（F1-F5 为游戏/截图保留，不占用）：
///   F11 标记当前位置为点 A | F12 标记当前位置为点 B（先选两处空地）
///   F9  角色传送到 A    | F10 角色传送到 B
///   F7  载具传送到 A    | F8  载具传送到 B（整车+玩家，保持驾驶）
///   F6  全量快照（含 A/B 标记坐标 + 载具渲染诊断 + 主视角取证）
/// v0.12 实证：透明=quad Renderer.isVisible=False（引擎剔除）；根因=传送后 vehicle.transform 与
///   quad（视觉板）被游戏平滑插值逻辑拉向中途 → 脱离玩家相机视锥。
/// v0.13 锚定机制：传送后 120 帧窗口内，检测 transform 偏离目标 >2m 即重写 rb+transform+quad 位置
///   （对抗游戏平滑逻辑）；补 vehicleCamera pos/rot 与 Camera.allCameras 枚举（找真正渲染 quad 的相机）。
/// </summary>
[BepInPlugin("com.zedzone.tool.teleportprobe", "TeleportProbe", "0.13.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;
    private Harmony _harmony;

    public override void Load()
    {
        Instance = this;
        L = Log;

        // SharedLog 注入
        SharedLog.Initialize(
            (m) => Log.LogError(m),
            (m) => Log.LogWarning(m),
            (m) => Log.LogInfo(m));

        // Harmony 挂钩：拦截 BasicVehicle.EnforcePhysicsSafety
        try
        {
            _harmony = new Harmony("com.zedzone.tool.teleportprobe");
            var target = AccessTools.Method(typeof(BasicVehicle), "EnforcePhysicsSafety");
            if (target != null)
            {
                var prefix = typeof(TeleportProbePatch)
                    .GetMethod(nameof(TeleportProbePatch.Prefix), BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log.LogInfo("[TeleportProbe] 已挂钩 BasicVehicle.EnforcePhysicsSafety (Prefix 跳过)");
            }
            else
            {
                Log.LogWarning("[TeleportProbe] 未找到 BasicVehicle.EnforcePhysicsSafety，硬修未生效");
            }
        }
        catch (Exception e)
        {
            Log.LogError($"[TeleportProbe] Harmony 挂钩异常: {e}");
        }

        // Harmony 挂钩 2：拦截 GameObject.SetActive(false) 关闭载具 camera（v0.9，透明根因防线）
        // 根因：游戏 chunk 系统在传送后（含 60 帧巡检窗口之后）会 deactivate vehicleCamera 的 GameObject
        try
        {
            var saTarget = AccessTools.Method(typeof(GameObject), "SetActive");
            if (saTarget != null)
            {
                var saPrefix = typeof(CameraSetActivePatch)
                    .GetMethod(nameof(CameraSetActivePatch.Prefix), BindingFlags.Public | BindingFlags.Static);
                _harmony.Patch(saTarget, prefix: new HarmonyMethod(saPrefix));
                Log.LogInfo("[TeleportProbe] 已挂钩 GameObject.SetActive (Prefix 拦截 vehicleCamera 关闭)");
            }
            else
            {
                Log.LogWarning("[TeleportProbe] 未找到 GameObject.SetActive，camera 关闭拦截未生效");
            }
        }
        catch (Exception e)
        {
            Log.LogError($"[TeleportProbe] GameObject.SetActive 挂钩异常: {e}");
        }

        AddComponent<ProbeComponent>();
        ProbeComponent.LoadMarks();
        Log.LogInfo("[TeleportProbe] 传送可行性探查已加载 (v0.13.0)");
    }
}

/// <summary>
/// Harmony 前缀：拦截 EnforcePhysicsSafety（IsActive 时跳过，防止回滚传送状态）。
/// v0.1 阶段默认放行（return true），预留结构待后续实现。
/// </summary>
public static class TeleportProbePatch
{
    public static bool Prefix()
    {
        if (ProbeComponent.IsActive)
        {
            return false;
        }
        return true;
    }
}

/// <summary>
/// v0.9 Harmony 前缀：拦截 GameObject.SetActive(false) 于被保护的 vehicleCamera GameObject。
/// 根因防线：游戏 chunk 系统在传送后任意时刻（含 v0.8 的 60 帧窗口之外）会 deactivate camera，
/// 导致 RTT 不渲染 → 车辆透明。此处直接阻止关闭，并记录调用来源（前几帧栈）。
/// </summary>
public static class CameraSetActivePatch
{
    public static bool Prefix(GameObject __instance, bool value)
    {
        var prot = ProbeComponent.WatchCameraGO;
        if (!value && prot != null && __instance != null && __instance.Pointer == prot.Pointer)
        {
            Plugin.L.LogWarning("[V-Tele] ★ 拦截 GameObject.SetActive(false) 于 vehicleCamera（阻止游戏关闭，camera 保持激活）");
            Plugin.L.LogWarning($"[V-Tele] SetActive(false) 调用栈: {BriefStack(6)}");
            return false; // 阻止关闭
        }
        return true;
    }

    /// <summary>截取调用栈前 N 行（跳过本方法自身几行）。</summary>
    private static string BriefStack(int maxLines)
    {
        try
        {
            var st = Environment.StackTrace;
            var lines = st.Split('\n');
            var sb = new StringBuilder();
            int taken = 0;
            for (int i = 0; i < lines.Length && taken < maxLines; i++)
            {
                var ln = lines[i].Trim();
                if (ln.Length == 0) continue;
                if (ln.Contains("CameraSetActivePatch") || ln.Contains("at System.Environment")) continue;
                sb.AppendLine(ln);
                taken++;
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"堆栈获取异常: {e.Message}";
        }
    }
}

/// <summary>
/// MonoBehaviour 运行时组件：监听键位输入，管理传送/快照状态。
/// v0.1 为最小骨架，功能桩待后续实现。
/// </summary>
public class ProbeComponent : MonoBehaviour
{
    /// <summary>传送激活标记，供 Harmony Prefix 读取。</summary>
    public static bool IsActive;

    /// <summary>SetVehicleChunk 延迟帧观测计数（v0.2）。</summary>
    private static int _chunkWatchFrames = 0;

    /// <summary>SetVehicleChunk 延迟帧观测对象（v0.2）。</summary>
    private static BasicVehicle _chunkWatchVehicle = null;

    /// <summary>传送后驾驶状态后验计数（v0.3，10 帧后确认 isDriving，捕获物理弹射）。</summary>
    private static int _driveWatchFrames = 0;

    /// <summary>传送后驾驶状态后验对象（v0.3）。</summary>
    private static object _driveWatchPlayer = null;

    /// <summary>v0.6 恢复验证阶段标记（true=已恢复，等待 5 帧验证）。</summary>
    private static bool _driveWatchRestored = false;

    /// <summary>v0.9 持续巡检帧计数器（每 10 帧检查一次，直到下车/下次传送）。</summary>
    private static int _renderWatchCounter = 0;

    /// <summary>v0.9 持续巡检对象。</summary>
    private static BasicVehicle _renderWatchVehicle = null;

    /// <summary>v0.9 被保护的 vehicleCamera GameObject（供 SetActive 拦截器比较）。</summary>
    internal static GameObject WatchCameraGO = null;

    /// <summary>v0.13 锚定窗口剩余帧数（传送后对抗游戏平滑插值逻辑）。</summary>
    private static int _anchorFrames = 0;

    /// <summary>v0.13 锚定目标载具。</summary>
    private static BasicVehicle _anchorVehicle = null;

    /// <summary>v0.13 锚定目标位置（世界，含地形 z）。</summary>
    private static Vector3 _anchorPos;

    /// <summary>v0.13 锚定目标 xy（rb 用）。</summary>
    private static Vector2 _anchorXY;

    /// <summary>v0.13 锚定 quad（视觉板，需同步钉位）。</summary>
    private static GameObject _anchorQuad = null;

    /// <summary>传送后驾驶状态后验载具（v0.4，渲染层诊断用）。</summary>
    private static BasicVehicle _driveWatchVehicle = null;

    /// <summary>标记点 A（v0.3，玩家选空地标记）。</summary>
    private static Vector3? MarkA = null;

    /// <summary>标记点 B（v0.3，玩家选空地标记）。</summary>
    private static Vector3? MarkB = null;

    private void Update()
    {
        // F11/F12：标记当前玩家位置为 A/B（先找两处空地）
        if (Input.GetKeyDown(KeyCode.F11))
        {
            MarkPoint("A");
        }
        if (Input.GetKeyDown(KeyCode.F12))
        {
            MarkPoint("B");
        }

        // F9/F10：角色传送到标记点 A/B
        if (Input.GetKeyDown(KeyCode.F9))
        {
            Plugin.L.LogInfo("[TeleportProbe] F9 角色传送到 A");
            TeleportToMark("A", false);
        }
        if (Input.GetKeyDown(KeyCode.F10))
        {
            Plugin.L.LogInfo("[TeleportProbe] F10 角色传送到 B");
            TeleportToMark("B", false);
        }

        // F7/F8：载具传送到标记点 A/B（整车+玩家，保持驾驶）
        if (Input.GetKeyDown(KeyCode.F7))
        {
            Plugin.L.LogInfo("[TeleportProbe] F7 载具传送到 A");
            TeleportToMark("A", true);
        }
        if (Input.GetKeyDown(KeyCode.F8))
        {
            Plugin.L.LogInfo("[TeleportProbe] F8 载具传送到 B");
            TeleportToMark("B", true);
        }

        // F6：全量快照
        if (Input.GetKeyDown(KeyCode.F6))
        {
            PrintSnapshot();
        }
    }

    private void LateUpdate()
    {
        // chunk 延迟帧观测：传送后连续 3 帧打印 currentChunk + frameFlag（覆盖未决项⑤）
        // v0.5：currentChunk 用深度读取（Reflect.Get 吞 getter 异常导致长期为空）
        // v0.6：frame=1 时若仍为空，dump BasicVehicle 全部含 chunk 成员（找真实字段名）
        if (_chunkWatchFrames > 0 && _chunkWatchVehicle != null)
        {
            int frameNo = 4 - _chunkWatchFrames;
            var chunkInfo = ReadCurrentChunk(_chunkWatchVehicle);
            var flag = Reflect.Get(_chunkWatchVehicle, "setVehicleChunkFrameFlag");
            Plugin.L.LogInfo($"[V-Tele] chunk观测 frame={frameNo}: currentChunk={chunkInfo} frameFlag={flag}");
            if (frameNo == 1 && (chunkInfo.EndsWith("=") || string.IsNullOrWhiteSpace(chunkInfo)))
            {
                var lines = DumpChunkMembers(_chunkWatchVehicle);
                foreach (var ln in lines) Plugin.L.LogInfo($"[V-Tele] chunk成员dump: {ln}");
            }
            _chunkWatchFrames--;
            if (_chunkWatchFrames == 0) _chunkWatchVehicle = null;
        }

        // v0.3：驾驶状态后验（传送后确认 isDriving，捕获物理弹射/车辆丢失）
        // v0.4：延长至 30 帧
        // v0.5：诊断升级（activeInHierarchy + 三件套）+ 检测到隐藏自动强制恢复
        // v0.6：状态机「30帧诊断 → 若异常恢复 → 5帧验证」；vehicleModel/Quad 真正可恢复
        if (_driveWatchFrames > 0 && _driveWatchPlayer != null)
        {
            _driveWatchFrames--;
            if (_driveWatchFrames == 0)
            {
                var drvVeh = Reflect.Get(_driveWatchPlayer, "drivingVehicle");
                Plugin.L.LogInfo($"[V-Tele] 驾驶状态后验(传送后30帧): isDriving={Reflect.Get(_driveWatchPlayer, "isDriving")} drivingVehicle={(drvVeh != null ? "非空" : "NULL")}");

                try
                {
                    var bv = _driveWatchVehicle;
                    if (bv != null)
                    {
                        RenderDiagVerbose(bv, _driveWatchRestored ? "恢复验证" : "后验30帧");
                        if (!_driveWatchRestored)
                        {
                            // 首次诊断：若三件套有隐藏 → 强制恢复 → 再等 5 帧验证
                            bool anyHidden = RestoreVehicleRender(bv);
                            if (anyHidden)
                            {
                                _driveWatchRestored = true;
                                _driveWatchFrames = 5;
                            }
                            else
                            {
                                _driveWatchPlayer = null;
                                _driveWatchVehicle = null;
                            }
                        }
                        else
                        {
                            _driveWatchPlayer = null;
                            _driveWatchVehicle = null;
                            _driveWatchRestored = false;
                        }
                    }
                    else
                    {
                        Plugin.L.LogWarning("[V-Tele] 渲染诊断: _driveWatchVehicle 为 null（载具实例可能已被游戏销毁/重建）");
                        _driveWatchPlayer = null;
                        _driveWatchVehicle = null;
                        _driveWatchRestored = false;
                    }
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"[V-Tele] 渲染诊断异常: {e.Message}");
                    _driveWatchPlayer = null;
                    _driveWatchVehicle = null;
                    _driveWatchRestored = false;
                }
            }
        }

        // v0.8 渲染巡检：传送后 60 帧窗口，每 10 帧检查三件套 activeInHierarchy，异常即恢复
        // v0.9 改为【持续巡检】：不限窗口，每 10 帧一次，直到下车/载具变化/下次传送为止
        // v0.10：正常状态静默（v0.9 实测 477 行无意义日志）；巡检升级取证：
        //   model/quad localScale、model Renderer enabled 数、camera cullingMask（基线比对）
        if (_renderWatchVehicle != null)
        {
            _renderWatchCounter++;
            if (_renderWatchCounter % 10 == 0)
            {
                var gc = GameController.instance;
                var pc = gc != null ? gc.playerCharacter : null;
                var drv = pc != null ? Reflect.Get(pc, "drivingVehicle") : null;
                if (drv == null || !ReferenceEquals(drv, _renderWatchVehicle))
                {
                    Plugin.L.LogInfo("[V-Tele] 巡检: 已下车或载具实例变化，停止持续监控");
                    _renderWatchVehicle = null;
                    WatchCameraGO = null;
                    return;
                }

                var camObj = Reflect.Get(_renderWatchVehicle, "vehicleCamera");
                var model = Reflect.Get(_renderWatchVehicle, "vehicleModel");
                var quad = Reflect.Get(_renderWatchVehicle, "vehicleQuadObj");
                bool camBad = camObj is Behaviour cb && !cb.gameObject.activeInHierarchy;
                var mgo = TryGetGameObject(model);
                var qgo = TryGetGameObject(quad);
                bool modelBad = mgo != null && !mgo.activeInHierarchy;
                bool quadBad = qgo != null && !qgo.activeInHierarchy;

                // v0.10 深度取证：localScale / renderer / cullingMask
                string deep = "";
                try
                {
                    if (mgo != null) deep += $" modelScale={mgo.transform.localScale}";
                    if (qgo != null) deep += $" quadScale={qgo.transform.localScale}";
                    int mDisabled = 0, mTotal = 0, mVisible = 0;
                    if (mgo != null)
                    {
                        var rs = mgo.GetComponentsInChildren<Renderer>(true);
                        mTotal = rs.Length;
                        foreach (var r in rs)
                        {
                            if (!r.enabled) mDisabled++;
                            try { if (r.isVisible) mVisible++; } catch { }
                        }
                    }
                    deep += $" modelR={mDisabled}/{mTotal}禁用";
                    // v0.12：isVisible（引擎实时可见性，直指玩家能否看到）
                    deep += $" modelV={mVisible}/{mTotal}可见";
                    // v0.11：model/quad 的 layer（主摄像机 cullingMask 是否覆盖的疑点）
                    if (mgo != null) deep += $" modelLayer={mgo.layer}";
                    if (qgo != null) deep += $" quadLayer={qgo.layer}";
                    if (qgo != null)
                    {
                        var qr = qgo.GetComponent<Renderer>();
                        if (qr != null) deep += $" quadV={qr.isVisible}";
                    }
                    if (camObj is Camera cm) deep += $" culling={cm.cullingMask}";
                }
catch (Exception e) { deep += $" 深度取证异常:{e.Message}"; }

                if (camBad || modelBad || quadBad)
                {
                    Plugin.L.LogWarning($"[V-Tele] 巡检(持续): camera={!camBad} model={!modelBad} quad={!quadBad}{deep}，触发恢复");
                    RestoreVehicleRender(_renderWatchVehicle);
                    // 恢复后立即补一次 RT 像素采样（若 camera 有 targetTexture）
                    SampleRT(_renderWatchVehicle, "恢复后");
                }
                else if (_renderWatchCounter % 60 == 0)
                {
                    // v0.10：每 60 帧静默打一次基线摘要（含 RT 采样），跟踪无异常时的状态漂移
                    Plugin.L.LogInfo($"[V-Tele] 巡检(基线): 正常{deep}");
                    SampleRT(_renderWatchVehicle, "基线");
                }
            }
        }

        // v0.13 锚定窗口：传送后 120 帧内，transform 偏离目标 >2m 即重写 rb+transform+quad
        // （v0.12 实证：游戏平滑插值逻辑把 vehicle/quad 拉向中途 → quad 脱离视锥 → 引擎剔除 → 透明）
        if (_anchorFrames > 0 && _anchorVehicle != null)
        {
            _anchorFrames--;
            try
            {
                var t = _anchorVehicle.transform;
                float drift = Vector3.Distance(new Vector3(t.position.x, t.position.y, 0f), new Vector3(_anchorPos.x, _anchorPos.y, 0f));
                if (drift > 2f)
                {
                    Plugin.L.LogWarning($"[V-Tele] 锚定: transform 偏离 {drift:F1}m，重写 rb+transform 至目标");
                    try { _anchorVehicle.m_rigidbody.position = _anchorXY; _anchorVehicle.m_rigidbody.velocity = Vector2.zero; } catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] 锚定 rb 异常: {e.Message}"); }
                    t.position = _anchorPos;
                    if (_anchorQuad != null) _anchorQuad.transform.position = _anchorPos;
                }
                else if (_anchorFrames % 30 == 0)
                {
                    Plugin.L.LogInfo($"[V-Tele] 锚定: 偏离 {drift:F2}m，稳定（窗口剩{_anchorFrames}帧）");
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[V-Tele] 锚定异常: {e.Message}");
                _anchorFrames = 0;
                _anchorVehicle = null;
                _anchorQuad = null;
            }
            if (_anchorFrames == 0)
            {
                _anchorVehicle = null;
                _anchorQuad = null;
            }
        }
    }


    /// <summary>
    /// v0.5 渲染链路深度诊断：Renderer 计数（enabled 禁用 + activeInHierarchy 隐藏）+ 三件套状态。
    /// v0.6 修复：vehicleModel/vehicleQuadObj 实为 GameObject（继承 UnityEngine.Object 而非 Component），
    ///   统一用 TryGetGameObject 取根对象并打印真实类型名。
    /// </summary>
    private static void RenderDiagVerbose(BasicVehicle bv, string tag)
    {
        try
        {
            var renders = bv.GetComponentsInChildren<Renderer>(true);
            int disabled = 0, inactiveHier = 0;
            foreach (var r in renders)
            {
                if (!r.enabled) disabled++;
                if (!r.gameObject.activeInHierarchy) inactiveHier++;
            }
            Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: Renderer总数={renders.Length} 禁用(enabled=false)={disabled} activeInHierarchy=false={inactiveHier}");

            var model = Reflect.Get(bv, "vehicleModel");
            var quad = Reflect.Get(bv, "vehicleQuadObj");
            var cam = Reflect.Get(bv, "vehicleCamera");
            string mType = model != null ? model.GetType().FullName : "NULL";
            var mgo = TryGetGameObject(model);
            if (mgo != null)
                Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: vehicleModel type={mType} activeSelf={mgo.activeSelf} activeInHierarchy={mgo.activeInHierarchy}");
            else
                Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: vehicleModel type={mType} 无法取 GameObject");

            string qType = quad != null ? quad.GetType().FullName : "NULL";
            var qgo = TryGetGameObject(quad);
            if (qgo != null)
            {
                var qr = qgo.GetComponent<Renderer>();
                Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: vehicleQuadObj type={qType} activeSelf={qgo.activeSelf} activeInHierarchy={qgo.activeInHierarchy} renderer.enabled={(qr != null ? qr.enabled.ToString() : "N/A")}");
            }
            else
                Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: vehicleQuadObj type={qType} 无法取 GameObject");

            if (cam is Behaviour cb)
                Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: vehicleCamera enabled={cb.enabled} activeSelf={cb.gameObject.activeSelf} activeInHierarchy={cb.gameObject.activeInHierarchy}");
            else
                Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: vehicleCamera type={(cam != null ? cam.GetType().FullName : "NULL")} 非Behaviour");

            // v0.7 深层诊断：层级正常但视觉仍透明 → 检查 RTT 纹理与材质层
            try
            {
                if (cam is Camera cm)
                    Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: camera.targetTexture={(cm.targetTexture != null ? $"{cm.targetTexture.width}x{cm.targetTexture.height}" : "NULL")} cullingMask={cm.cullingMask}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] 渲染诊断[{tag}] camera 深层异常: {e.Message}"); }
            try
            {
                var qgo2 = TryGetGameObject(quad);
                var qr2 = qgo2 != null ? qgo2.GetComponent<Renderer>() : null;
                if (qr2 != null && qr2.material != null)
                {
                    var mat = qr2.material;
                    string tex = "NULL";
                    try { tex = mat.mainTexture != null ? $"{mat.mainTexture.width}x{mat.mainTexture.height}" : "NULL"; } catch { }
                    float a = 1f;
                    try { if (mat.HasProperty("_Color")) a = mat.color.a; } catch { }
                    Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: quad材质 shader={(mat.shader != null ? mat.shader.name : "NULL")} mainTexture={tex} color.a={a:F2}");
                }
                else
                {
                    Plugin.L.LogInfo($"[V-Tele] 渲染诊断[{tag}]: quad材质 renderer 或 material 为空");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] 渲染诊断[{tag}] quad 材质异常: {e.Message}"); }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[V-Tele] 渲染诊断[{tag}] 异常: {e.Message}");
        }
    }

    /// <summary>
    /// v0.6 统一取 GameObject：GameObject 本身 或 Component.gameObject。
    /// </summary>
    private static GameObject TryGetGameObject(object obj)
    {
        if (obj == null) return null;
        if (obj is GameObject go) return go;
        if (obj is Component c) return c.gameObject;
        return null;
    }

    /// <summary>
    /// v0.5 传送后渲染恢复：若三件套被游戏隐藏（chunk 重复进入场景），强制 SetActive(true)/enabled=true 恢复。
    /// v0.6 修复：vehicleModel/vehicleQuadObj 走 GameObject 路径；返回是否有隐藏被处理（供验证状态机）。
    /// </summary>
    private static bool RestoreVehicleRender(BasicVehicle bv)
    {
        bool changed = false;
        try
        {
            var model = Reflect.Get(bv, "vehicleModel");
            var mgo = TryGetGameObject(model);
            if (mgo != null && !mgo.activeInHierarchy)
            {
                Plugin.L.LogWarning($"[V-Tele] ★ vehicleModel({model.GetType().Name}) 被隐藏 (activeInHierarchy=false)，强制 SetActive(true) 恢复");
                mgo.SetActive(true);
                changed = true;
            }
            else if (mgo == null && model != null)
            {
                Plugin.L.LogWarning($"[V-Tele] vehicleModel type={model.GetType().FullName} 无法取 GameObject，跳过恢复");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] vehicleModel 恢复异常: {e.Message}"); }

        try
        {
            var quad = Reflect.Get(bv, "vehicleQuadObj");
            var qgo = TryGetGameObject(quad);
            if (qgo != null && !qgo.activeInHierarchy)
            {
                Plugin.L.LogWarning($"[V-Tele] ★ vehicleQuadObj({quad.GetType().Name}) 被隐藏 (activeInHierarchy=false)，强制 SetActive(true) 恢复");
                qgo.SetActive(true);
                changed = true;
            }
            else if (qgo == null && quad != null)
            {
                Plugin.L.LogWarning($"[V-Tele] vehicleQuadObj type={quad.GetType().FullName} 无法取 GameObject，跳过恢复");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] vehicleQuadObj 恢复异常: {e.Message}"); }

        try
        {
            var camObj = Reflect.Get(bv, "vehicleCamera");
            if (camObj is Behaviour cb)
            {
                if (!cb.enabled || !cb.gameObject.activeInHierarchy)
                {
                    Plugin.L.LogWarning($"[V-Tele] ★ vehicleCamera 异常 (enabled={cb.enabled} activeInHierarchy={cb.gameObject.activeInHierarchy})，强制恢复");
                    cb.gameObject.SetActive(true);
                    cb.enabled = true;
                    changed = true;
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] vehicleCamera 恢复异常: {e.Message}"); }

        if (changed) Plugin.L.LogInfo("[V-Tele] ★ 渲染链路已强制恢复（5 帧后自动验证）");
        return changed;
    }

    /// <summary>
    /// v0.5 currentChunk 深度读取：Reflect.Get 会吞掉 getter 异常导致长期为空，
    /// 这里直接反射并保留异常详情。
    /// </summary>
    private static string ReadCurrentChunk(object v)
    {
        try
        {
            var t = v.GetType();
            var p = t.GetProperty("currentChunk", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null)
            {
                try { return $"prop={p.GetValue(v)}"; }
                catch (Exception e) { return $"prop-getter异常={e.GetType().Name}:{e.Message}"; }
            }
            var f = t.GetField("currentChunk", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                try { return $"field={f.GetValue(v)}"; }
                catch (Exception e) { return $"field读取异常={e.GetType().Name}:{e.Message}"; }
            }
            return "无字段/属性";
        }
        catch (Exception e) { return $"读取异常={e.Message}"; }
    }

    /// <summary>
    /// v0.6 列出对象上所有含 "chunk" 的成员名与当前值（定位 currentChunk 空值根因：字段名不对 or 值本身为空）。
    /// </summary>
    private static List<string> DumpChunkMembers(object v)
    {
        var lines = new List<string>();
        try
        {
            var t = v.GetType();
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var m in members)
            {
                if (m.Name.IndexOf("chunk", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string val = "?";
                try
                {
                    if (m is PropertyInfo p) val = p.GetValue(v)?.ToString() ?? "<null>";
                    else if (m is FieldInfo f) val = f.GetValue(v)?.ToString() ?? "<null>";
                }
                catch (Exception e) { val = $"异常:{e.GetType().Name}:{e.Message}"; }
                lines.Add($"{m.MemberType}: {m.Name} = {val}");
            }
            if (lines.Count == 0) lines.Add("无含 chunk 的成员");
        }
        catch (Exception e) { lines.Add($"dump 异常: {e.Message}"); }
        return lines;
    }

    /// <summary>
    /// F6 全量快照：打印玩家 + 载具的位置/物理/chunk/坐标等取证信息。
    /// 反射采用字段→属性→get_ 三级查找，IL2CPP 安全。
    /// </summary>
    private void PrintSnapshot()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("[TeleportProbe] ===== F6 全量快照 =====");
            sb.AppendLine($"  [isActive] ProbeComponent.IsActive = {IsActive}");
            sb.AppendLine($"  [标记点] A = {(MarkA.HasValue ? F(MarkA.Value) : "未标记")}  B = {(MarkB.HasValue ? F(MarkB.Value) : "未标记")}");

            var gc = GameController.instance;
            sb.AppendLine($"  GameController.instance = {(gc != null ? "OK" : "NULL")}");
            if (gc == null) { Plugin.L.LogInfo(sb.ToString()); return; }

            var pc = gc.playerCharacter;
            sb.AppendLine($"  player = {(pc != null ? pc.name : "NULL")}");

            if (pc != null)
            {
                // ── 玩家基础信息 ──
                try { sb.AppendLine($"  [玩家] transform.position = {F(pc.transform.position)}"); }
                catch (Exception e) { sb.AppendLine($"  [玩家] transform.position 异常: {e.Message}"); }

                // 玩家 Rigidbody2D：优先 Reflect.Get("m_rigidbody")，回退 GetComponent
                try
                {
                    var rb2dObj = Reflect.Get(pc, "m_rigidbody");
                    var rb2d = rb2dObj as Rigidbody2D ?? pc.GetComponent<Rigidbody2D>();
                    if (rb2d != null)
                        sb.AppendLine($"  [玩家] rb2d: pos={rb2d.position} vel={rb2d.velocity} kinematic={rb2d.isKinematic} gravity={rb2d.gravityScale}");
                    else
                        sb.AppendLine("  [玩家] rb2d: N/A (Reflect.Get 与 GetComponent 均为空)");
                }
                catch (Exception e) { sb.AppendLine($"  [玩家] rb2d 异常: {e.Message}"); }

                // isDriving / drivingVehicle
                try
                {
                    var isDriving = Reflect.Get(pc, "isDriving");
                    sb.AppendLine($"  [玩家] isDriving = {isDriving ?? "N/A"}");
                }
                catch (Exception e) { sb.AppendLine($"  [玩家] isDriving 异常: {e.Message}"); }

                object drivingVehicle = null;
                try
                {
                    drivingVehicle = Reflect.Get(pc, "drivingVehicle");
                    sb.AppendLine($"  [玩家] drivingVehicle = {(drivingVehicle != null ? $"{drivingVehicle.GetType().Name}({drivingVehicle})" : "NULL")}");
                }
                catch (Exception e) { sb.AppendLine($"  [玩家] drivingVehicle 异常: {e.Message}"); }

                // ── 载具信息（驾驶中） ──
                if (drivingVehicle != null)
                {
                    var v = drivingVehicle as BasicVehicle;
                    if (v == null)
                    {
                        sb.AppendLine($"  [载具] BasicVehicle cast 失败, type={drivingVehicle.GetType().FullName}");
                    }
                    else
                    {
                        sb.AppendLine($"  [载具] name = {v.name}");

                        // transform.position
                        try { sb.AppendLine($"  [载具] transform.position = {F(v.transform.position)}"); }
                        catch (Exception e) { sb.AppendLine($"  [载具] transform.position 异常: {e.Message}"); }

                        // Rigidbody2D（m_rigidbody 是 BasicVehicle 公开字段）
                        try
                        {
                            var rf = v.m_rigidbody;
                            if (rf != null)
                                sb.AppendLine($"  [载具] rb2d: pos={rf.position} vel={rf.velocity} kinematic={rf.isKinematic} gravity={rf.gravityScale} simulated={rf.simulated}");
                            else
                                sb.AppendLine("  [载具] rb2d: NULL");
                        }
                        catch (Exception e) { sb.AppendLine($"  [载具] rb2d 异常: {e.Message}"); }

                        // 物理安全字段（Reflect.Get 反射）
                        try { sb.AppendLine($"  [载具] isPhysicsAnomalous = {Reflect.Get(v, "isPhysicsAnomalous") ?? "N/A"}"); }
                        catch (Exception e) { sb.AppendLine($"  [载具] isPhysicsAnomalous 异常: {e.Message}"); }
                        try { sb.AppendLine($"  [载具] maxSaneStepDistance = {Reflect.Get(v, "maxSaneStepDistance") ?? "N/A"}"); }
                        catch (Exception e) { sb.AppendLine($"  [载具] maxSaneStepDistance 异常: {e.Message}"); }
                        try { sb.AppendLine($"  [载具] lastSanePosition = {Reflect.Get(v, "lastSanePosition") ?? "N/A"}"); }
                        catch (Exception e) { sb.AppendLine($"  [载具] lastSanePosition 异常: {e.Message}"); }
                        try { sb.AppendLine($"  [载具] realVelocity = {Reflect.Get(v, "realVelocity") ?? "N/A"}"); }
                        catch (Exception e) { sb.AppendLine($"  [载具] realVelocity 异常: {e.Message}"); }
                        try { sb.AppendLine($"  [载具] rigidVelocity = {Reflect.Get(v, "rigidVelocity") ?? "N/A"}"); }
                        catch (Exception e) { sb.AppendLine($"  [载具] rigidVelocity 异常: {e.Message}"); }

                        // vehicleData + 坐标
                        try
                        {
                            var vd = Reflect.Get(v, "vehicleData");
                            if (vd != null)
                            {
                                sb.AppendLine($"  [载具] vehicleData = OK (type={vd.GetType().FullName})");
                                sb.AppendLine($"  [载具] vehicleData.worldCoordinate = {Reflect.Get(vd, "worldCoordinate") ?? "N/A"}");
                                sb.AppendLine($"  [载具] vehicleData.coordinateX = {Reflect.Get(vd, "coordinateX") ?? "N/A"}");
                                sb.AppendLine($"  [载具] vehicleData.coordinateY = {Reflect.Get(vd, "coordinateY") ?? "N/A"}");
                            }
                            else
                                sb.AppendLine("  [载具] vehicleData = NULL");
                        }
                        catch (Exception e) { sb.AppendLine($"  [载具] vehicleData 异常: {e.Message}"); }

                        // currentChunk + chunk 坐标
                        try
                        {
                            var chunk = Reflect.Get(v, "currentChunk");
                            if (chunk != null)
                            {
                                sb.AppendLine($"  [载具] currentChunk = {chunk} (type={chunk.GetType().FullName})");
                                // 尝试多种可能的坐标字段名
                                var chunkX = Reflect.Get(chunk, "x") ?? Reflect.Get(chunk, "coordinateX") ?? Reflect.Get(chunk, "position");
                                var chunkY = Reflect.Get(chunk, "y") ?? Reflect.Get(chunk, "coordinateY");
                                if (chunkX != null) sb.AppendLine($"  [载具] chunk.x/coordinateX = {chunkX}");
                                if (chunkY != null) sb.AppendLine($"  [载具] chunk.y/coordinateY = {chunkY}");
                                var chunkPos = Reflect.Get(chunk, "position") ?? Reflect.Get(chunk, "chunkPosition");
                                if (chunkPos != null && chunkPos != chunkX) sb.AppendLine($"  [载具] chunk.position/chunkPosition = {chunkPos}");
                            }
                            else
                                sb.AppendLine("  [载具] currentChunk = NULL");
                        }
                        catch (Exception e) { sb.AppendLine($"  [载具] currentChunk 异常: {e.Message}"); }

                        // setVehicleChunkFrameFlag（延迟帧标记）
                        try { sb.AppendLine($"  [载具] setVehicleChunkFrameFlag = {Reflect.Get(v, "setVehicleChunkFrameFlag") ?? "N/A"}"); }
                        catch (Exception e) { sb.AppendLine($"  [载具] setVehicleChunkFrameFlag 异常: {e.Message}"); }

                        // v0.2 增强：地形高度对比（transform.z vs GetGroundHeight）
                        try
                        {
                            float gh = GetGroundHeight(new Vector2(v.transform.position.x, v.transform.position.y));
                            float dz = Mathf.Abs(v.transform.position.z - gh);
                            sb.AppendLine($"  [载具] 地形高度对比: transform.z={v.transform.position.z:F2} vs GetGroundHeight={gh:F2} 差异={dz:F2}");
                        }
                        catch (Exception e) { sb.AppendLine($"  [载具] 地形高度对比异常: {e.Message}"); }

                        // v0.2 增强：rb.position vs transform.position 一致性
                        try
                        {
                            var vp = v.transform.position;
                            var rp = v.m_rigidbody.position;
                            bool consistent = Mathf.Abs(vp.x - rp.x) < 0.01f && Mathf.Abs(vp.y - rp.y) < 0.01f;
                            sb.AppendLine($"  [载具] rb/transform 一致性 = {consistent} (transform=({vp.x:F2},{vp.y:F2}) rb=({rp.x:F2},{rp.y:F2}))");
                        }
                        catch (Exception e) { sb.AppendLine($"  [载具] rb/transform 一致性异常: {e.Message}"); }

                        // v0.6 增强：F6 中也输出渲染链路诊断（读档后可随时手动查车辆透明状态）
                        RenderDiagVerbose(v, "F6");
                        // v0.12：F6 追加主视角取证（透明后可手动复查 layer/位置/isVisible）
                        DumpModelRenderState(v, "F6");
                    }
                }
            }

            // ── v0.2 增强：MapController chunk 激活探测（无论是否驾驶，覆盖未决项②） ──
            try
            {
                var mc = MapController.instance;
                if (mc != null)
                {
                    string[] chunkFieldCandidates = { "activeChunkDic", "activeChunkList", "chunkDic", "allChunkDic", "chunkList", "activeChunk" };
                    bool found = false;
                    foreach (var fn in chunkFieldCandidates)
                    {
                        var val = Reflect.Get(mc, fn);
                        if (val != null)
                        {
                            sb.AppendLine($"  [MapController] {fn} = {val} (type={val.GetType().FullName})");
                            found = true;
                            break;
                        }
                    }
                    if (!found) sb.AppendLine("  [MapController] 未发现 chunk 激活字段（候选字段名全部为空）");
                }
                else
                {
                    sb.AppendLine("  [MapController] instance = NULL");
                }
            }
            catch (Exception e) { sb.AppendLine($"  [MapController] 探测异常: {e.Message}"); }

            Plugin.L.LogInfo(sb.ToString());
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TeleportProbe] PrintSnapshot 顶层异常: {e}");
        }
    }

    /// <summary>
    /// 角色传送核心逻辑（v0.3 标记点版）：获取玩家 HCC → 直接写目标坐标 → 查地形高度 → rb 双写 → transform 写入。
    /// </summary>
    private static void TryTeleportPlayer(Vector2 targetXY)
    {
        try
        {
            var gc = GameController.instance;
            if (gc == null) { Plugin.L.LogWarning("[TeleportProbe] GameController.instance 为 null"); return; }
            var pc = gc.playerCharacter;
            if (pc == null) { Plugin.L.LogWarning("[TeleportProbe] playerCharacter 为 null"); return; }

            // v0.2：IsActive 闭环开启（与 F7 载具传送保持一致；玩家路径无 EnforcePhysicsSafety，防御性开启）
            IsActive = true;

            var trans = pc.transform;
            if (trans == null) { Plugin.L.LogWarning("[TeleportProbe] playerCharacter.transform 为 null"); IsActive = false; return; }

            Vector3 oldPos = trans.position;
            Plugin.L.LogInfo($"[TeleportProbe] 传送前坐标: ({oldPos.x:F2}, {oldPos.y:F2}, {oldPos.z:F2})");
            Plugin.L.LogInfo($"[TeleportProbe] 目标坐标(标记点): ({targetXY.x:F2}, {targetXY.y:F2})");

            float groundZ = oldPos.z;
            try
            {
                var mc = MapController.instance;
                if (mc != null)
                {
                    groundZ = mc.GetTerrainTempHeightByWorldPosition(new Vector2(targetXY.x, targetXY.y));
                    Plugin.L.LogInfo($"[TeleportProbe] 地形高度 z={groundZ:F2} (目标xy=({targetXY.x:F2},{targetXY.y:F2}))");
                }
                else
                {
                    Plugin.L.LogWarning("[TeleportProbe] MapController.instance 为 null，使用旧 z");
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[TeleportProbe] GetTerrainTempHeightByWorldPosition 异常: {e.Message}，使用旧 z={oldPos.z}");
            }

            Vector3 newPos = new Vector3(targetXY.x, targetXY.y, groundZ);
            Plugin.L.LogInfo($"[TeleportProbe] 目标坐标: ({newPos.x:F2}, {newPos.y:F2}, {newPos.z:F2})");

            bool rbWritten = false;
            try
            {
                Rigidbody2D rb = null;
                try { rb = trans.GetComponent<Rigidbody2D>(); } catch { }
                if (rb == null)
                {
                    string[] rbFieldNames = { "m_rigidbody", "rb", "rigidbody2D", "_rigidbody", "rigidbody" };
                    foreach (var fieldName in rbFieldNames)
                    {
                        var rbObj = Reflect.Get(pc, fieldName);
                        if (rbObj is Rigidbody2D reflectedRb)
                        {
                            rb = reflectedRb;
                            Plugin.L.LogInfo($"[TeleportProbe] rb 通过反射获取成功: 字段名={fieldName}");
                            break;
                        }
                    }
                }
                if (rb != null)
                {
                    rb.position = new Vector2(newPos.x, newPos.y);
                    rb.velocity = Vector2.zero;
                    rbWritten = true;
                    Plugin.L.LogInfo($"[TeleportProbe] rb 双写完成: position=({newPos.x:F2},{newPos.y:F2}), velocity=zero");
                }
                else
                {
                    Plugin.L.LogInfo("[TeleportProbe] 未找到 Rigidbody2D，跳过 rb 双写（玩家可能无物理组件）");
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[TeleportProbe] rb 双写异常: {e.Message}（不影响 transform 写入）");
            }

            trans.position = newPos;
            Plugin.L.LogInfo($"[TeleportProbe] transform.position 已写入: ({newPos.x:F2}, {newPos.y:F2}, {newPos.z:F2})");
            Plugin.L.LogInfo($"[TeleportProbe] ★ 传送完成: ({oldPos.x:F2},{oldPos.y:F2},{oldPos.z:F2}) -> ({newPos.x:F2},{newPos.y:F2},{newPos.z:F2})  rb双写={rbWritten}");

            // v0.2：IsActive 闭环关闭
            IsActive = false;
            Plugin.L.LogInfo("[TeleportProbe] IsActive ← false（Harmony 窗口关闭）");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TeleportProbe] TryTeleportPlayer 顶层异常: {e}");
            IsActive = false; // 异常时确保关闭
        }
    }

    /// <summary>
    /// F7/F8 载具传送（v0.3 标记点版）：整车+玩家一起传，保持驾驶状态（路线 B）。
    /// 三件套：(1) IsActive 闭环 (2) rb+transform+lastSane (3) VehicleData+chunk
    /// 依据：teleport-v02-vehicle-plan.md 第四节（TryTeleportVehicle 分步流程）。
    /// </summary>
    private static void TryTeleportVehicle(Vector2 target)
    {
        try
        {
            var gc = GameController.instance;
            if (gc == null) { Plugin.L.LogWarning("[V-Tele] GameController null"); return; }
            var pc = gc.playerCharacter;
            if (pc == null) { Plugin.L.LogWarning("[V-Tele] playerCharacter null"); return; }

            // Step 0: 获取载具（drivingVehicle 非 null = 驾驶中）
            var v = Reflect.Get(pc, "drivingVehicle") as BasicVehicle;
            if (v == null) { Plugin.L.LogWarning("[V-Tele] 未在驾驶状态，F7/F8 无效"); return; }
            Plugin.L.LogInfo($"[V-Tele] 载具获取: {v.name}");

            // Step 1: 快照传送前状态（用于 F6 对比）
            Vector3 beforePos = v.transform.position;
            Vector2 beforeRbPos = v.m_rigidbody.position;
            object beforeLastSane = Reflect.Get(v, "lastSanePosition");
            object beforeMaxStep = Reflect.Get(v, "maxSaneStepDistance");
            var vdObj = Reflect.Get(v, "vehicleData");
            object beforeVdWorld = vdObj != null ? Reflect.Get(vdObj, "worldCoordinate") : null;
            object beforeChunk = Reflect.Get(v, "currentChunk");
            Plugin.L.LogInfo($"[V-Tele] 传送前: pos={F(beforePos)} rb=({beforeRbPos.x:F2},{beforeRbPos.y:F2}) " +
                $"lastSane={beforeLastSane} maxStep={beforeMaxStep} vd={beforeVdWorld} chunk={beforeChunk}");

            // Step 2: 目标坐标（v0.3 直接使用标记点）+ 地形高度
            Vector2 targetXY = target;
            float groundZ = beforePos.z;
            try
            {
                var mc = MapController.instance;
                if (mc != null)
                    groundZ = mc.GetTerrainTempHeightByWorldPosition(targetXY);
                else
                    Plugin.L.LogWarning("[V-Tele] MapController null，沿用旧 z");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[V-Tele] 地形高度查询异常: {e.Message}，沿用旧 z");
            }
            Vector3 newPos = new Vector3(targetXY.x, targetXY.y, groundZ);
            Plugin.L.LogInfo($"[V-Tele] 目标: {F(newPos)}  地形z={groundZ:F2}");

            // Step 3: ★IsActive 闭环开启（Harmony 窗口）
            ProbeComponent.IsActive = true;
            Plugin.L.LogInfo("[V-Tele] IsActive ← true（Harmony 窗口开启）");

            // Step 4: ★maxSaneStepDistance 放大到 100（原值记录，Step 9 恢复——v0.4 修复未恢复 bug）
            float origMaxStep = 100f;
            try
            {
                var mv = Reflect.Get(v, "maxSaneStepDistance");
                if (mv is float mf) origMaxStep = mf;
                bool ok = Reflect.Set(v, "maxSaneStepDistance", 100f);
                Plugin.L.LogInfo($"[V-Tele] maxSaneStepDistance ← 100（原 {beforeMaxStep}）ok={ok}");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[V-Tele] maxSaneStepDistance 写入异常: {e.Message}");
            }

            // Step 5: ★rb + transform 三件套写入（先 rb 后 transform）
            try
            {
                v.m_rigidbody.position = new Vector2(targetXY.x, targetXY.y);
                v.m_rigidbody.velocity = Vector2.zero;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[V-Tele] rb 写入异常: {e.Message}");
            }
            v.transform.position = newPos;
            Plugin.L.LogInfo($"[V-Tele] 三件套: rb.pos=({targetXY.x:F2},{targetXY.y:F2}) rb.vel=zero transform={F(newPos)}");

            // Step 6: ★lastSanePosition 同步到目标点
            try
            {
                bool ok = Reflect.Set(v, "lastSanePosition", v.transform.position);
                Plugin.L.LogInfo($"[V-Tele] lastSanePosition ← {F(v.transform.position)} ok={ok}");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[V-Tele] lastSanePosition 写入异常: {e.Message}");
            }

            // Step 7: ★VehicleData 坐标同步（worldCoordinate + coordinateX/Y）
            if (vdObj != null)
            {
                try
                {
                    Reflect.Set(vdObj, "worldCoordinate", new Vector2(targetXY.x, targetXY.y));
                    Reflect.Set(vdObj, "coordinateX", targetXY.x);
                    Reflect.Set(vdObj, "coordinateY", targetXY.y);
                    Plugin.L.LogInfo($"[V-Tele] VehicleData 同步: world=({targetXY.x:F2},{targetXY.y:F2}) x={targetXY.x:F2} y={targetXY.y:F2}");
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"[V-Tele] VehicleData 同步异常: {e.Message}");
                }
            }
            else
            {
                Plugin.L.LogWarning("[V-Tele] vehicleData 为 null，跳过坐标同步");
            }

            // Step 8: ★SetVehicleChunk() + 延迟帧观测启动（连续 3 帧打印 chunk + frameFlag）
            try
            {
                v.SetVehicleChunk();
                Plugin.L.LogInfo("[V-Tele] SetVehicleChunk() 已调用");
                var flagNow = Reflect.Get(v, "setVehicleChunkFrameFlag");
                Plugin.L.LogInfo($"[V-Tele] setVehicleChunkFrameFlag = {flagNow}");
                _chunkWatchFrames = 3;
                _chunkWatchVehicle = v;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[V-Tele] SetVehicleChunk 异常: {e.Message}");
            }

            // Step 9: ★IsActive 闭环关闭 + maxSaneStepDistance 恢复原值（v0.4）
            ProbeComponent.IsActive = false;
            Plugin.L.LogInfo("[V-Tele] IsActive ← false（Harmony 窗口关闭）");
            try
            {
                bool ok = Reflect.Set(v, "maxSaneStepDistance", origMaxStep);
                Plugin.L.LogInfo($"[V-Tele] maxSaneStepDistance 恢复 ← {origMaxStep} ok={ok}");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"[V-Tele] maxSaneStepDistance 恢复异常: {e.Message}");
            }

            // Step 10: ★传送后验证日志 + 驾驶状态检查
            Vector3 afterPos = v.transform.position;
            Vector2 afterRbPos = v.m_rigidbody.position;
            object afterVdWorld = vdObj != null ? Reflect.Get(vdObj, "worldCoordinate") : null;
            Plugin.L.LogInfo($"[V-Tele] ★ 传送完成: {F(beforePos)} → {F(afterPos)}  rb=({afterRbPos.x:F2},{afterRbPos.y:F2}) vd={afterVdWorld}  isActive=false");

            object isDriving = Reflect.Get(pc, "isDriving");
            object drvVeh = Reflect.Get(pc, "drivingVehicle");
            Plugin.L.LogInfo($"[V-Tele] 驾驶状态: isDriving={isDriving} drivingVehicle={(drvVeh != null ? "非空" : "NULL")}");

            // v0.3：启动驾驶状态后验（捕获物理弹射/车辆丢失）
            // v0.4：30 帧（v0.3 实测异常发生在 10 帧后），并记录载具实例用于渲染诊断
            _driveWatchFrames = 30;
            _driveWatchPlayer = pc;
            _driveWatchVehicle = v;

            // v0.8：传送完成【立即】诊断+恢复（不等 30 帧，避免用户已看到透明）；
            // v0.9：启动持续巡检（每 10 帧一次，直到下车），并登记 camera 供 SetActive 拦截器保护
            // v0.10：附加 RT 像素采样 + 可见性成员 dump
            // v0.11：附加主视角取证（model layer/位置/材质 + 主摄像机）
            RenderDiagVerbose(v, "传送后立即");
            SampleRT(v, "传送后立即");
            DumpModelRenderState(v, "传送后立即");
            var visLines = DumpVisibilityMembers(v);
            foreach (var ln in visLines) Plugin.L.LogInfo($"[V-Tele] 可见性成员: {ln}");
            RestoreVehicleRender(v);
            _renderWatchCounter = 0;
            _renderWatchVehicle = v;
            WatchCameraGO = TryGetGameObject(Reflect.Get(v, "vehicleCamera"));

            // v0.13：启动锚定窗口（120 帧，偏离>2m 重写 rb+transform+quad，对抗游戏平滑插值）
            _anchorFrames = 120;
            _anchorVehicle = v;
            _anchorPos = newPos;
            _anchorXY = targetXY;
            _anchorQuad = TryGetGameObject(Reflect.Get(v, "vehicleQuadObj"));
            Plugin.L.LogInfo($"[V-Tele] 锚定窗口启动: 目标=({newPos.x:F2},{newPos.y:F2},{newPos.z:F2}) 120帧");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[V-Tele] TryTeleportVehicle 顶层异常: {e}");
            ProbeComponent.IsActive = false; // 异常时确保关闭
        }
    }

    /// <summary>
    /// v0.3：按标记点传送（isVehicle=false 人传 / true 车传）。
    /// </summary>
    private static void TeleportToMark(string label, bool isVehicle)
    {
        Vector3? mark = label == "A" ? MarkA : MarkB;
        if (!mark.HasValue)
        {
            string key = label == "A" ? "F11" : "F12";
            Plugin.L.LogWarning($"[TeleportProbe] 标记点 {label} 未设置，请先走到空地按 {key} 标记");
            return;
        }
        Vector2 targetXY = new Vector2(mark.Value.x, mark.Value.y);
        if (isVehicle)
        {
            Plugin.L.LogInfo($"[TeleportProbe] → 载具传送到 {label} ({targetXY.x:F2},{targetXY.y:F2})");
            TryTeleportVehicle(targetXY);
        }
        else
        {
            Plugin.L.LogInfo($"[TeleportProbe] → 角色传送到 {label} ({targetXY.x:F2},{targetXY.y:F2})");
            TryTeleportPlayer(targetXY);
        }
    }

    /// <summary>
    /// v0.3：标记当前玩家位置为点 A/B（玩家应站在目标空地）。
    /// </summary>
    private static void MarkPoint(string label)
    {
        try
        {
            var gc = GameController.instance;
            if (gc == null) { Plugin.L.LogWarning("[TeleportProbe] GameController.instance 为 null"); return; }
            var pc = gc.playerCharacter;
            if (pc == null) { Plugin.L.LogWarning("[TeleportProbe] playerCharacter 为 null"); return; }
            var pos = pc.transform.position;
            float z = GetGroundHeight(new Vector2(pos.x, pos.y));
            var mark = new Vector3(pos.x, pos.y, z);
            if (label == "A") MarkA = mark; else MarkB = mark;
            Plugin.L.LogInfo($"[TeleportProbe] ★ 标记点 {label} = {F(mark)}（玩家位置 ({pos.x:F2},{pos.y:F2})，地形z={z:F2}）");
            SaveMarks();
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TeleportProbe] MarkPoint 异常: {e.Message}");
        }
    }

    /// <summary>
    /// v0.7 标记点持久化：写入 plugins/TeleportProbe/marks.txt。
    /// 正式版计划以可建造传送地毯建筑替代，此为探针期临时方案。
    /// </summary>
    internal static string MarksPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "marks.txt");

    internal static void SaveMarks() {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MarksPath));
            var lines = new List<string>
            {
                $"A={(MarkA.HasValue ? $"{MarkA.Value.x},{MarkA.Value.y},{MarkA.Value.z}" : "")}",
                $"B={(MarkB.HasValue ? $"{MarkB.Value.x},{MarkB.Value.y},{MarkB.Value.z}" : "")}",
            };
            System.IO.File.WriteAllLines(MarksPath, lines);
            Plugin.L.LogInfo($"[TeleportProbe] 标记点已保存到 {MarksPath}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TeleportProbe] SaveMarks 异常: {e.Message}");
        }
    }

    internal static void LoadMarks()
    {
        try
        {
            if (!System.IO.File.Exists(MarksPath))
            {
                Plugin.L.LogInfo($"[TeleportProbe] 无标记点存档文件（{MarksPath}），AB 未标记");
                return;
            }
            foreach (var line in System.IO.File.ReadAllLines(MarksPath))
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var label = line.Substring(0, idx).Trim();
                var valStr = line.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(valStr)) continue;
                var parts = valStr.Split(',');
                if (parts.Length != 3) continue;
                if (!float.TryParse(parts[0], out float x) ||
                    !float.TryParse(parts[1], out float y) ||
                    !float.TryParse(parts[2], out float z))
                    continue;
                var mark = new Vector3(x, y, z);
                if (label == "A") { MarkA = mark; Plugin.L.LogInfo($"[TeleportProbe] 已恢复标记点 A = {F(mark)}"); }
                else if (label == "B") { MarkB = mark; Plugin.L.LogInfo($"[TeleportProbe] 已恢复标记点 B = {F(mark)}"); }
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TeleportProbe] LoadMarks 异常: {e.Message}");
        }
    }

    /// <summary>
    /// v0.11 主视角取证：主摄像机直接渲染 vehicleModel（RT 采样恒定证明 quad 管线非主视角）。
    /// dump vehicleModel/vehicleQuadObj 的 layer/世界位置/材质明细 + 主摄像机 cullingMask。
    /// </summary>
    private static void DumpModelRenderState(BasicVehicle bv, string tag)
    {
        try
        {
            // 主摄像机
            try
            {
                var mainCam = Camera.main;
                if (mainCam != null)
                    Plugin.L.LogInfo($"[V-Tele] 主摄像机[{tag}]: cullingMask={mainCam.cullingMask} enabled={mainCam.enabled} activeInHierarchy={mainCam.gameObject.activeInHierarchy} pos={mainCam.transform.position} rot={mainCam.transform.eulerAngles}");
                else
                    Plugin.L.LogInfo($"[V-Tele] 主摄像机[{tag}]: Camera.main = NULL");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] 主摄像机[{tag}] 异常: {e.Message}"); }

            // v0.13：vehicleCamera（RTT 相机）位置 + 全部摄像机枚举（找真正渲染 quad 的相机）
            try
            {
                var vc = Reflect.Get(bv, "vehicleCamera");
                if (vc is Behaviour vb)
                    Plugin.L.LogInfo($"[V-Tele] vehicleCamera[{tag}]: enabled={vb.enabled} pos={vb.transform.position} rot={vb.transform.eulerAngles}");
                else
                    Plugin.L.LogInfo($"[V-Tele] vehicleCamera[{tag}]: 不可用 (type={(vc != null ? vc.GetType().FullName : "NULL")})");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] vehicleCamera[{tag}] 异常: {e.Message}"); }

            try
            {
                var cams = Camera.allCameras;
                if (cams != null)
                {
                    foreach (var c in cams)
                    {
                        if (c == null) continue;
                        Plugin.L.LogInfo($"[V-Tele] 摄像机枚举[{tag}]: {c.name} culling={c.cullingMask} enabled={c.enabled} pos={c.transform.position}");
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[V-Tele] 摄像机枚举[{tag}] 异常: {e.Message}"); }

            var model = Reflect.Get(bv, "vehicleModel");
            var quad = Reflect.Get(bv, "vehicleQuadObj");
            var refPos = bv.transform.position;
            DumpGoRenderDetail("vehicleModel", model, tag, refPos);
            DumpGoRenderDetail("vehicleQuadObj", quad, tag, refPos);
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[V-Tele] DumpModelRenderState[{tag}] 异常: {e.Message}");
        }
    }

    /// <summary>v0.11 单个 GameObject 的渲染细节：layer/位置/材质/子 renderer 分布。</summary>
    /// <param name="refPos">车辆 transform 世界位置（计算相对偏移，判断 model/quad 是否被移走）。</param>
    private static void DumpGoRenderDetail(string label, object obj, string tag, Vector3? refPos = null)
    {
        try
        {
            var go = TryGetGameObject(obj);
            if (go == null)
            {
                Plugin.L.LogInfo($"[V-Tele] {label}[{tag}]: 无法取 GameObject (type={(obj != null ? obj.GetType().FullName : "NULL")})");
                return;
            }
            var t = go.transform;
            var mainRenderer = go.GetComponent<Renderer>();
            string matInfo = "N/A";
            if (mainRenderer != null && mainRenderer.sharedMaterial != null)
            {
                var m = mainRenderer.sharedMaterial;
                float a = 1f;
                try { if (m.HasProperty("_Color")) a = m.color.a; } catch { }
                matInfo = $"shader={m.shader?.name ?? "NULL"} queue={m.renderQueue} alpha={a:F2}";
            }
            string offset = "";
            if (refPos.HasValue) offset = $" off=({t.position.x - refPos.Value.x:F1},{t.position.y - refPos.Value.y:F1},{t.position.z - refPos.Value.z:F1})";
            Plugin.L.LogInfo($"[V-Tele] {label}[{tag}]: layer={go.layer} pos={t.position}{offset} localScale={t.localScale} renderer={(mainRenderer != null ? $"enabled={mainRenderer.enabled} isVisible={mainRenderer.isVisible}" : "无")} {matInfo}");

            // 子 renderer 的 layer 分布 + 可见性(isVisible) + 材质摘要（聚合打印，避免行数爆炸）
            var renders = go.GetComponentsInChildren<Renderer>(true);
            var layerCounts = new Dictionary<int, int>();
            int visibleCount = 0, enabledCount = 0;
            foreach (var r in renders)
            {
                int l = r.gameObject.layer;
                if (layerCounts.ContainsKey(l)) layerCounts[l]++;
                else layerCounts[l] = 1;
                if (r.isVisible) visibleCount++;
                if (r.enabled) enabledCount++;
            }
            var layerStr = new StringBuilder();
            foreach (var kv in layerCounts) layerStr.Append($"layer{kv.Key}x{kv.Value} ");
            Plugin.L.LogInfo($"[V-Tele] {label}[{tag}]: 子Renderer={renders.Length} isVisible={visibleCount} enabled={enabledCount} 分布[{layerStr}]");
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[V-Tele] {label}[{tag}] dump 异常: {e.Message}");
        }
    }

    /// <summary>
    /// v0.10 RT 像素采样：验证 vehicleCamera 的 RenderTexture 是否渲染出内容
    /// （v0.9 实证 camera 保持 active 但车辆仍透明 → 「RT 空白」成为头号嫌疑）。
    /// 5x5 网格采样，统计非透明像素占比 + 平均色。
    /// </summary>
    private static void SampleRT(BasicVehicle bv, string tag)
    {
        try
        {
            var camObj = Reflect.Get(bv, "vehicleCamera");
            if (!(camObj is Camera cm) || cm.targetTexture == null)
            {
                Plugin.L.LogInfo($"[V-Tele] RT采样[{tag}]: targetTexture=null 或 camera 不可用");
                return;
            }
            var rt = cm.targetTexture;
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                try
                {
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    const int grid = 5;
                    int samples = 0, opaque = 0;
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int ix = 0; ix < grid; ix++)
                    {
                        for (int iy = 0; iy < grid; iy++)
                        {
                            int px = (int)((ix + 0.5f) * rt.width / grid);
                            int py = (int)((iy + 0.5f) * rt.height / grid);
                            var c = tex.GetPixel(px, py);
                            samples++;
                            if (c.a > 0.05f) opaque++;
                            r += c.r; g += c.g; b += c.b; a += c.a;
                        }
                    }
                    Plugin.L.LogInfo($"[V-Tele] RT采样[{tag}]: {rt.width}x{rt.height} 采样{samples}点 非透明={opaque} 平均色=({r / samples:F2},{g / samples:F2},{b / samples:F2},{a / samples:F2})");
                }
                finally
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[V-Tele] RT采样[{tag}] 异常: {e.Message}");
        }
    }

    /// <summary>
    /// v0.10 列出 BasicVehicle 上可见性相关成员（visible/show/hide/display），
    /// 寻找游戏独立的车辆显示/隐藏 API（透明疑似走独立可见性系统，不走 SetActive）。
    /// </summary>
    private static List<string> DumpVisibilityMembers(object v)
    {
        var lines = new List<string>();
        try
        {
            var t = v.GetType();
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var m in members)
            {
                var n = m.Name;
                if (n.IndexOf("visible", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("show", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("hide", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("display", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("render", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("layer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string val = "?";
                try
                {
                    if (m is PropertyInfo p) val = p.GetValue(v)?.ToString() ?? "<null>";
                    else if (m is FieldInfo f) val = f.GetValue(v)?.ToString() ?? "<null>";
                }
                catch (Exception e) { val = $"异常:{e.GetType().Name}"; }
                lines.Add($"{m.MemberType}: {n} = {val}");
            }
            if (lines.Count == 0) lines.Add("无 visible/show/hide/display 成员");
        }
        catch (Exception e) { lines.Add($"dump 异常: {e.Message}"); }
        return lines;
    }

    /// <summary>
    /// 获取当前驾驶车辆（桩，待后续用 Reflect 三级反射实现）。
    /// </summary>
    public static object CurrentVehicle()
    {
        try
        {
            var gc = GameController.instance;
            if (gc == null) return null;
            var pc = gc.playerCharacter;
            if (pc == null) return null;
            return Reflect.Get(pc, "drivingVehicle");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TeleportProbe] CurrentVehicle 异常: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取地面高度（z 轴数据层）。
    /// 复用 FlyProbe:844-857 模式：MapController.instance.GetTerrainTempHeightByWorldPosition。
    /// </summary>
    public static float GetGroundHeight(Vector2 pos)
    {
        try
        {
            var mc = MapController.instance;
            if (mc == null) return 0f;
            return mc.GetTerrainTempHeightByWorldPosition(new Vector2(pos.x, pos.y));
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[TeleportProbe] GetGroundHeight 异常: {e.Message}");
            return 0f;
        }
    }

    /// <summary>格式化 Vector3 为简短字符串。</summary>
    private static string F(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
}
