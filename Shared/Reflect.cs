using System;
using System.Reflection;

namespace ZedZoneShared;

/// <summary>
/// Il2CppInterop 运行时反射辅助：成员可能是字段、属性或 set_ 方法，
/// 统一按 字段 → 属性 → set_方法 的顺序查找读写。
/// 日志经 SharedLog 注入（各 mod Plugin.Load 时设置，共享库不耦合具体插件）。
/// </summary>
public static class Reflect
{
    private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static object Get(object obj, string name)
    {
        if (obj == null) return null;
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

    public static bool Set(object obj, string name, object value)
    {
        if (obj == null) return false;
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
        if (setter != null)
        {
            try
            {
                var pt = setter.GetParameters()[0].ParameterType;
                setter.Invoke(obj, new[] { Convert(value, pt) });
                return true;
            }
            catch (Exception e) { SharedLog.Error($"set_{t.Name}.{name} 失败: {e.Message}"); return false; }
        }

        SharedLog.Warning($"成员不可写: {t.Name}.{name}");
        return false;
    }

    public static object Convert(object value, Type target)
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
        return value;
    }
}
