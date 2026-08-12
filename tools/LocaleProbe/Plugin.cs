using System;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace LocaleProbe;

/// <summary>
/// 语言/本地化可行性探查 v0.1.0：
/// 回答三个 MOD 英文适配的关键问题：
/// A. 当前语言如何检测（GameSettingsData / LanguageRegistry / ModLocaleManager）
/// B. 原版物品 ItemAttr 各语言字段/ getter 在当前语言下的取值
/// C. 模拟 mod 注入的 ItemAttr（itemName/itemName_Runtime=中文）的 ItemName/ItemName_EN getter 行为
/// D. 临时切换 gameLanguage 后 getter 是否实时语言感知（决定是否需要监听语言切换）
/// E. ModLocaleManager 静态方法存在性（语言切换监听可行性）
/// F. ItemAttr 语言相关字段的存储类型
/// 启动 15 秒后自动探查一次，F9 可重复。
/// </summary>
[BepInPlugin("com.zedzone.tool.localeprobe", "LocaleProbe", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource L;

    public override void Load()
    {
        Instance = this;
        L = Log;
        AddComponent<ProbeComponent>();
        Log.LogInfo("[LocaleProbe] 探查插件已加载 (v0.1.0)");
    }
}

public class ProbeComponent : MonoBehaviour
{
    private float _probeTimer = 15f;
    private bool _probeDone;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9))
        {
            try { RunProbe("F9 手动探查"); }
            catch (Exception e) { Plugin.L.LogError($"[LocaleProbe] F9 异常: {e}"); }
        }
        if (_probeDone) return;
        _probeTimer -= Time.deltaTime;
        if (_probeTimer > 0f) return;
        _probeDone = true;
        try { RunProbe("启动自动探查"); }
        catch (Exception e) { Plugin.L.LogError($"[LocaleProbe] 自动探查异常: {e}"); }
    }

    private void RunProbe(string tag)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[LocaleProbe] ===== 语言/本地化探查 [{tag}] =====");

        // ---------- A. 语言检测入口 ----------
        try
        {
            var mgr = GameSettingsDataManager.instance;
            sb.AppendLine($"A. GameSettingsDataManager.instance = {(mgr != null ? "OK" : "null")}");
            if (mgr != null)
            {
                var settings = mgr.LoadGameSettingsData();
                if (settings != null)
                {
                    sb.AppendLine($"   gameLanguage = {settings.gameLanguage}");
                    sb.AppendLine($"   languageCode = {settings.languageCode}");
                }
                else sb.AppendLine("   LoadGameSettingsData() = null");
            }
        }
        catch (Exception e) { sb.AppendLine($"A. GameSettingsDataManager 异常: {e}"); }

        try { sb.AppendLine($"   LanguageRegistry.IsCurrentChinese() = {LanguageRegistry.IsCurrentChinese()}"); }
        catch (Exception e) { sb.AppendLine($"   IsCurrentChinese 异常: {e.Message}"); }

        try
        {
            var settings = GameSettingsDataManager.instance?.LoadGameSettingsData();
            sb.AppendLine($"   ModLocaleManager.ResolveCurrentLangCode = {ModLocaleManager.ResolveCurrentLangCode(settings)}");
            if (settings != null)
                sb.AppendLine($"   ModLocaleManager.GameLanguageToLangCode({settings.gameLanguage}) = {ModLocaleManager.GameLanguageToLangCode(settings.gameLanguage)}");
        }
        catch (Exception e) { sb.AppendLine($"   ModLocaleManager 语言解析异常: {e.Message}"); }

        try { sb.AppendLine($"   LanguageRegistry.CnCode = {RefGetStatic("LanguageRegistry", "CnCode")} | EnCode = {RefGetStatic("LanguageRegistry", "EnCode")}"); }
        catch (Exception e) { sb.AppendLine($"   CnCode/EnCode 异常: {e.Message}"); }
        try
        {
            sb.AppendLine($"   LanguageRegistry.FindByEnum(English)={LanguageRegistry.FindByEnum(GameLanguage.English)?.code} | FindByEnum(SC)={LanguageRegistry.FindByEnum(GameLanguage.SimplifiedChinese)?.code}");
        }
        catch (Exception e) { sb.AppendLine($"   FindByEnum 异常: {e.Message}"); }

        // ---------- B. 原版物品语言字段（当前语言下） ----------
        var mgr2 = ItemManager.instance;
        if (mgr2 == null)
        {
            sb.AppendLine("B. ItemManager 未就绪");
        }
        else
        {
            foreach (var id in new[] { 0, 85, 205, 91 })
            {
                try
                {
                    var attr = mgr2.GetItemAttrById(id);
                    if (attr == null) { sb.AppendLine($"B. 原版物品 {id}: 不存在"); continue; }
                    sb.AppendLine($"B. 原版物品 {id} [{attr.GetType().FullName}]");
                    sb.AppendLine($"   itemName            = {attr.itemName}");
                    sb.AppendLine($"   itemName_Runtime    = {attr.itemName_Runtime}");
                    sb.AppendLine($"   ItemName(getter)    = {attr.ItemName}");
                    sb.AppendLine($"   ItemName_EN(getter) = {attr.ItemName_EN}");
                    sb.AppendLine($"   itemDescription              = {Trunc(attr.itemDescription)}");
                    sb.AppendLine($"   itemDescription_WithLanguage = {Trunc(attr.itemDescription_WithLanguage)}");
                    sb.AppendLine($"   runtimeItemDescription       = {Trunc(attr.runtimeItemDescription)}");
                    sb.AppendLine($"   ItemDescription(getter)      = {Trunc(attr.ItemDescription)}");
                    sb.AppendLine($"   ItemDescription_EN(getter)   = {Trunc(attr.ItemDescription_EN)}");
                    sb.AppendLine($"   cachedDescLang               = {Trunc(attr.cachedDescLang)}");
                    try { sb.AppendLine($"   GetDescription() = {Trunc(attr.GetDescription())}"); }
                    catch (Exception ex) { sb.AppendLine($"   GetDescription() 异常: {ex.Message}"); }
                }
                catch (Exception e) { sb.AppendLine($"B. 原版物品 {id} 异常: {e.Message}"); }
            }
        }

        // ---------- C. 模拟 mod 注入 ----------
        try
        {
            var attr = new ItemAttr();
            SetMember(attr, "itemId", 910000);
            SetMember(attr, "itemName", "测试中文名");
            SetMember(attr, "itemName_Runtime", "测试中文名");
            SetMember(attr, "itemDescription", "测试中文描述");
            SetMember(attr, "itemDescription_WithLanguage", "测试中文描述");

            sb.AppendLine("C. 模拟 mod ItemAttr（仅字段设置，未注册）:");
            sb.AppendLine($"   ItemName(getter)        = {attr.ItemName}");
            sb.AppendLine($"   ItemName_EN(getter)     = {attr.ItemName_EN}");
            sb.AppendLine($"   itemName_Runtime        = {attr.itemName_Runtime}");
            sb.AppendLine($"   ItemDescription(getter) = {Trunc(attr.ItemDescription)}");
            sb.AppendLine($"   ItemDescription_EN      = {Trunc(attr.ItemDescription_EN)}");
            try { sb.AppendLine($"   GetDescription()        = {Trunc(attr.GetDescription())}"); }
            catch (Exception ex) { sb.AppendLine($"   GetDescription() 异常: {ex.Message}"); }

            // 真实注入到 itemAttrDic（同 mod 流程）
            try
            {
                mgr2.itemAttrDic.Add(910000, attr);
                sb.AppendLine("C2. 已注入 itemAttrDic[910000]");
            }
            catch (Exception ex) { sb.AppendLine($"C2. 注入 itemAttrDic 异常: {ex.Message}（尝试反射注入）"); RefDicAdd(mgr2, attr); }

            var reread = ItemManager.instance.GetItemAttrById(910000);
            if (reread != null)
            {
                sb.AppendLine("C2b. 重新读取 910000:");
                sb.AppendLine($"   ItemName(getter)    = {reread.ItemName}");
                sb.AppendLine($"   ItemName_EN(getter) = {reread.ItemName_EN}");
                sb.AppendLine($"   itemName_Runtime    = {reread.itemName_Runtime}");
                sb.AppendLine($"   ItemDescription     = {Trunc(reread.ItemDescription)}");
                sb.AppendLine($"   ItemDescription_EN  = {Trunc(reread.ItemDescription_EN)}");
                sb.AppendLine($"   itemDescription_WithLanguage = {Trunc(reread.itemDescription_WithLanguage)}");

                try
                {
                    reread.InitItemAttr();
                    sb.AppendLine("C3. InitItemAttr() 后:");
                    sb.AppendLine($"   ItemName(getter)        = {reread.ItemName}");
                    sb.AppendLine($"   ItemName_EN(getter)     = {reread.ItemName_EN}");
                    sb.AppendLine($"   itemName_Runtime        = {reread.itemName_Runtime}");
                    sb.AppendLine($"   runtimeItemDescription  = {Trunc(reread.runtimeItemDescription)}");
                    sb.AppendLine($"   itemDescription_WithLanguage = {Trunc(reread.itemDescription_WithLanguage)}");
                }
                catch (Exception ex) { sb.AppendLine($"C3. InitItemAttr 异常: {ex.Message}"); }
            }
        }
        catch (Exception e) { sb.AppendLine($"C. 模拟注入异常: {e}"); }

        // ---------- D. 临时切换语言验证 getter 实时性 ----------
        try
        {
            var settings = GameSettingsDataManager.instance?.LoadGameSettingsData();
            if (settings == null) sb.AppendLine("D. 无 settings，跳过");
            else
            {
                var orig = settings.gameLanguage;
                sb.AppendLine($"D. 语言切换实时性测试（当前 gameLanguage={orig}）:");
                settings.gameLanguage = GameLanguage.English;
                try
                {
                    sb.AppendLine($"   切换后 IsCurrentChinese() = {LanguageRegistry.IsCurrentChinese()}");
                    var a0 = ItemManager.instance?.GetItemAttrById(0);
                    if (a0 != null)
                        sb.AppendLine($"   切 English 后 原版0: ItemName={a0.ItemName} | ItemName_EN={a0.ItemName_EN} | itemName_Runtime={a0.itemName_Runtime}");
                    var aT = ItemManager.instance?.GetItemAttrById(910000);
                    if (aT != null)
                        sb.AppendLine($"   切 English 后 测试910000: ItemName={aT.ItemName} | ItemName_EN={aT.ItemName_EN} | itemName_Runtime={aT.itemName_Runtime}");
                }
                finally
                {
                    settings.gameLanguage = orig;
                    sb.AppendLine($"   （已恢复 gameLanguage={settings.gameLanguage}）");
                }
            }
        }
        catch (Exception e) { sb.AppendLine($"D. 切换测试异常: {e.Message}"); }

        // ---------- E. ModLocaleManager 静态方法存在性 ----------
        try
        {
            sb.AppendLine("E. ModLocaleManager 静态方法存在性:");
            foreach (var m in new[] { "ApplyLocaleToAttr", "ReapplyAllLocales", "GetForCurrentLanguage", "GetForLang", "LoadFromDir", "LoadAllFromDir", "Save", "ResolveCurrentLangCode", "GameLanguageToLangCode", "BcpToModLangCode" })
            {
                var mi = typeof(ModLocaleManager).GetMethod(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                sb.AppendLine($"   {m}: {(mi != null ? "存在" : "不存在")}");
            }
        }
        catch (Exception e) { sb.AppendLine($"E. ModLocaleManager 异常: {e.Message}"); }

        // ---------- F. ItemAttr 语言字段存储类型 ----------
        try
        {
            sb.AppendLine("F. ItemAttr 语言相关成员（interop 反射）:");
            foreach (var n in new[] { "itemName", "itemName_Runtime", "itemDescription", "itemDescription_WithLanguage", "runtimeItemDescription", "cachedDescLang", "ItemName", "ItemName_EN", "ItemDescription", "ItemDescription_EN" })
            {
                var t = typeof(ItemAttr);
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) sb.AppendLine($"   PROP {p.PropertyType.FullName} {n} {{ get={(p.GetMethod != null)} set={(p.SetMethod != null)} }}");
                else if (f != null) sb.AppendLine($"   FIELD {f.FieldType.FullName} {n}");
                else sb.AppendLine($"   无成员 {n}");
            }
        }
        catch (Exception e) { sb.AppendLine($"F. 成员类型异常: {e.Message}"); }

        Plugin.L.LogInfo(sb.ToString());
    }

    // ---------- 辅助 ----------

    private static object RefGetStatic(string typeName, string prop)
    {
        var t = Type.GetType($"{typeName}, Assembly-CSharp");
        var p = t?.GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return p?.GetValue(null);
    }

    private static void RefDicAdd(object mgr, ItemAttr attr)
    {
        try
        {
            var dic = mgr.GetType().GetProperty("itemAttrDic", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(mgr);
            var add = dic?.GetType().GetMethod("Add", new[] { typeof(int), typeof(object) })
                  ?? dic?.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            add?.Invoke(dic, new object[] { 910000, attr });
        }
        catch (Exception e) { Plugin.L.LogError($"[LocaleProbe] 反射注入失败: {e.Message}"); }
    }

    private static void SetMember(object o, string name, object val)
    {
        var t = o.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null) { p.SetValue(o, val); return; }
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) { f.SetValue(o, val); return; }
        Plugin.L.LogWarning($"[LocaleProbe] 无法设置 {t.Name}.{name}");
    }

    private static string Trunc(object v)
    {
        var s = v?.ToString() ?? "<null>";
        return s.Length > 150 ? s.Substring(0, 150) + "..." : s;
    }
}
