using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace ZedZoneShared;

/// <summary>
/// Il2CppInterop 运行时反射辅助：成员可能是字段、属性或 set_ 方法，
/// 统一按 字段 → 属性 → set_方法 的顺序查找读写。
/// 日志经 SharedLog 注入（各 mod Plugin.Load 时设置，共享库不耦合具体插件）。
/// P1-11A（2026-09-04）：MemberInfo 常驻缓存——Get/Set 各持一张
/// ConcurrentDictionary&lt;(Type, 成员名), MemberSlot&gt;，解析一次后复用，热路径零反射；
/// 找不到也记负缓存（Member 为 null），避免重复反射探路。解析顺序与回退语义与原来完全一致。
/// </summary>
public static class Reflect
{
    private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // P1-11A：Get 与 Set 分开存（同一成员名在两者下解析结果可能不同：
    // Get 命中只读属性即可取值，Set 要求可写才命中；Set 存的就是可写 Member）。
    // Member 为 null = 负缓存（该组合解析不到成员）。槽对象不可变，并发下 GetOrAdd 复用安全。
    private sealed class MemberSlot
    {
        public readonly MemberInfo Member;
        public MemberSlot(MemberInfo member) { Member = member; }
    }

    private static readonly ConcurrentDictionary<(Type, string), MemberSlot> GetCache =
        new ConcurrentDictionary<(Type, string), MemberSlot>();
    private static readonly ConcurrentDictionary<(Type, string), MemberSlot> SetCache =
        new ConcurrentDictionary<(Type, string), MemberSlot>();

    // Get 解析：字段 → 属性（只要存在即命中；无 getter 的写属性取值抛异常时吞掉返回 null，与原来一致）。
    private static MemberSlot ResolveGet(Type t, string name)
    {
        try
        {
            var f = t.GetField(name, AnyInst);
            if (f != null) return new MemberSlot(f);
            var p = t.GetProperty(name, AnyInst);
            if (p != null) return new MemberSlot(p);
            return new MemberSlot(null);
        }
        catch { return new MemberSlot(null); }
    }

    // Set 解析：字段 → 可写属性 → set_方法（不可写属性不命中，继续落到 set_ 查找，与原来一致）。
    private static MemberSlot ResolveSet(Type t, string name)
    {
        try
        {
            var f = t.GetField(name, AnyInst);
            if (f != null) return new MemberSlot(f);
            var p = t.GetProperty(name, AnyInst);
            if (p != null && p.CanWrite) return new MemberSlot(p);
            var setter = t.GetMethod("set_" + name, AnyInst);
            if (setter != null) return new MemberSlot(setter);
            return new MemberSlot(null);
        }
        catch { return new MemberSlot(null); }
    }

    public static object Get(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var t = obj.GetType();
            var m = GetCache.GetOrAdd((t, name), k => ResolveGet(k.Item1, k.Item2)).Member;
            if (m is FieldInfo f) return f.GetValue(obj);
            if (m is PropertyInfo p)
            {
                try { return p.GetValue(obj); }
                catch { }
                return null;
            }
            return null;
        }
        catch
        {
            // 缓存层自身异常时的回退：原直查逻辑，保证找不到成员时返回 null 的行为不变。
            return DirectGet(obj, name);
        }
    }

    // 原 Get 直查逻辑（仅缓存层异常时走，逐字保留原语义）。
    private static object DirectGet(object obj, string name)
    {
        try
        {
            var t = obj.GetType();
            var f = t.GetField(name, AnyInst);
            if (f != null) return f.GetValue(obj);
            var p = t.GetProperty(name, AnyInst);
            if (p != null)
            {
                try { return p.GetValue(obj); }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    public static bool Set(object obj, string name, object value)
    {
        if (obj == null) return false;
        try
        {
            var t = obj.GetType();
            var m = SetCache.GetOrAdd((t, name), k => ResolveSet(k.Item1, k.Item2)).Member;

            if (m is FieldInfo f)
            {
                try { f.SetValue(obj, Convert(value, f.FieldType)); return true; }
                catch (Exception e) { SharedLog.Error($"SetField {t.Name}.{name} 失败: {e.Message}"); return false; }
            }

            if (m is PropertyInfo p)
            {
                // 解析器只缓存可写属性；CanWrite 再判一次（防 Il2Cpp 代理类型热变），
                // 若真不可写则按原语义继续落到 set_ 方法查找。
                if (p.CanWrite)
                {
                    try { p.SetValue(obj, Convert(value, p.PropertyType)); return true; }
                    catch (Exception e) { SharedLog.Error($"SetProp {t.Name}.{name} 失败: {e.Message}"); return false; }
                }
                var fallback = SafeGetSetter(t, name);
                if (fallback != null) return InvokeSetter(obj, t, name, value, fallback);
                SharedLog.Warning($"成员不可写: {t.Name}.{name}");
                return false;
            }

            if (m is MethodInfo setter) return InvokeSetter(obj, t, name, value, setter);

            SharedLog.Warning($"成员不可写: {t.Name}.{name}");
            return false;
        }
        catch
        {
            // 缓存层自身异常时的回退：原直查逻辑，保证找不到成员时 Warning + false 的行为不变。
            return DirectSet(obj, name, value);
        }
    }

    private static MethodInfo SafeGetSetter(Type t, string name)
    {
        try { return t.GetMethod("set_" + name, AnyInst); }
        catch { return null; }
    }

    // 原 set_ 方法调用逻辑（含无参 set_ 时 GetParameters()[0] 越界同样进 catch 打 Error，与原来一致）。
    private static bool InvokeSetter(object obj, Type t, string name, object value, MethodInfo setter)
    {
        try
        {
            var pt = setter.GetParameters()[0].ParameterType;
            setter.Invoke(obj, new[] { Convert(value, pt) });
            return true;
        }
        catch (Exception e) { SharedLog.Error($"set_{t.Name}.{name} 失败: {e.Message}"); return false; }
    }

    // 原 Set 直查逻辑（仅缓存层异常时走，逐字保留原语义）。
    private static bool DirectSet(object obj, string name, object value)
    {
        try
        {
            var t = obj.GetType();

            var f = t.GetField(name, AnyInst);
            if (f != null)
            {
                try { f.SetValue(obj, Convert(value, f.FieldType)); return true; }
                catch (Exception e) { SharedLog.Error($"SetField {t.Name}.{name} 失败: {e.Message}"); return false; }
            }

            var p = t.GetProperty(name, AnyInst);
            if (p != null && p.CanWrite)
            {
                try { p.SetValue(obj, Convert(value, p.PropertyType)); return true; }
                catch (Exception e) { SharedLog.Error($"SetProp {t.Name}.{name} 失败: {e.Message}"); return false; }
            }

            var setter = t.GetMethod("set_" + name, AnyInst);
            if (setter != null) return InvokeSetter(obj, t, name, value, setter);

            SharedLog.Warning($"成员不可写: {t.Name}.{name}");
            return false;
        }
        catch { return false; }
    }

    public static object Convert(object value, Type target)
    {
        if (value == null) return null;
        if (target.IsInstanceOfType(value)) return value;
        // L 修复（2026-08-31）：Il2Cpp 包装类型（数组/List 等）透传原样——BCL 转换会抛 IConvertible 类异常
        // （实例：ItemAttr_Deployable.directionSprites = Il2CppReferenceArray<Sprite> 曾静默/异常失败）
        if (value.GetType() != null && value.GetType().Namespace != null && value.GetType().Namespace.StartsWith("Il2Cpp"))
            return value;
        if (target.IsEnum) return Enum.ToObject(target, value);
        if (target == typeof(float)) return System.Convert.ToSingle(value);
        if (target == typeof(int)) return System.Convert.ToInt32(value);
        if (target == typeof(double)) return System.Convert.ToDouble(value);
        if (target == typeof(bool)) return System.Convert.ToBoolean(value);
        if (target == typeof(long)) return System.Convert.ToInt64(value);
        if (target == typeof(string)) return System.Convert.ToString(value);
        return value;
    }
}
