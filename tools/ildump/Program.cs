using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static int Main(string[] args)
    {
        string interopDir = "D:\\SteamLibrary\\steamapps\\common\\ZED ZONE\\BepInEx\\interop";
        string coreDir = "D:\\SteamLibrary\\steamapps\\common\\ZED ZONE\\BepInEx\\core";
        string assembly = "Assembly-CSharp.dll";
        string search = null;
        string typeName = null;
        bool members = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--search": search = args[++i]; break;
                case "--type": typeName = args[++i]; break;
                case "--members": members = true; break;
                case "--asm": assembly = args[++i]; break;
            }
        }

        var paths = Directory.GetFiles(interopDir, "*.dll").ToList();
        // 补充 .NET 运行时程序集目录（System.Runtime 等，interop 程序集引用它们）
        string dotnetRoot = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (dotnetRoot != null && Directory.Exists(dotnetRoot))
            paths.AddRange(Directory.GetFiles(dotnetRoot, "*.dll"));
        // 补充 BepInEx core 目录（Il2CppInterop.Runtime 等）
        if (Directory.Exists(coreDir))
            paths.AddRange(Directory.GetFiles(coreDir, "*.dll"));
        var resolver = new PathAssemblyResolver(paths.Distinct().ToArray());
        using var mlc = new MetadataLoadContext(resolver, "Il2Cppmscorlib");

        string asmPath;
        if (File.Exists(assembly)) asmPath = Path.GetFullPath(assembly);
        else if (File.Exists(Path.Combine(interopDir, assembly))) asmPath = Path.Combine(interopDir, assembly);
        else asmPath = Path.Combine(coreDir, assembly);
        var asm = mlc.LoadFromAssemblyPath(asmPath);

        if (typeName != null)
        {
            var t = asm.GetType(typeName, false);
            if (t == null)
            {
                Console.WriteLine($"类型未找到: {typeName}");
                var fuzzy = asm.GetTypes().Where(x => x.FullName != null && x.FullName.Contains(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
                Console.WriteLine($"模糊匹配 {fuzzy.Count} 个:");
                foreach (var f in fuzzy.Take(30)) Console.WriteLine("  " + f.FullName);
                return 1;
            }
            DumpType(t, members);
            return 0;
        }

        var types = asm.GetTypes()
            .Where(t => search == null || (t.FullName != null && t.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.FullName)
            .ToList();

        Console.WriteLine($"Assembly: {assembly}  匹配类型数: {types.Count}");
        foreach (var t in types)
        {
            string baseName = t.BaseType?.Name ?? "";
            Console.WriteLine($"{t.FullName}  :  {baseName}");
        }
        return 0;
    }

    static void DumpType(Type t, bool members)
    {
        Console.WriteLine($"=== {t.FullName} ===");
        Console.WriteLine($"BaseType: {t.BaseType?.FullName}");
        Console.WriteLine($"Attributes: {t.Attributes}");

        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            try { Console.WriteLine($"  CTOR {FormatMethod(c)}"); }
            catch { Console.WriteLine($"  CTOR {c.Name} (sig-err)"); }
        }

        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            try { Console.WriteLine($"  FIELD {(f.IsPublic ? "public" : f.IsPrivate ? "private" : f.IsFamily ? "protected" : "internal")} {f.FieldType.Name} {f.Name}"); }
            catch { Console.WriteLine($"  FIELD {f.Name} (type-err)"); }
        }

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            try
            {
                var get = p.GetMethod; var set = p.SetMethod;
                string acc = (get?.IsPublic ?? false) || (set?.IsPublic ?? false) ? "public" : "nonpublic";
                Console.WriteLine($"  PROP {acc} {p.PropertyType.Name} {p.Name} {{ {(get != null ? "get; " : "")}{(set != null ? "set; " : "")}}}");
            }
            catch { Console.WriteLine($"  PROP {p.Name} (type-err)"); }
        }

        if (members)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                try { Console.WriteLine($"  METHOD {FormatMethod(m)}"); }
                catch { Console.WriteLine($"  METHOD {m.Name} (sig-err)"); }
            }
        }
    }

    static string FormatMethod(MethodBase m)
    {
        var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        string ret = m is MethodInfo mi ? mi.ReturnType.Name : "void";
        string acc = m.IsPublic ? "public" : m.IsPrivate ? "private" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : "?";
        string stat = m.IsStatic ? "static " : "";
        return $"{acc} {stat}{ret} {m.Name}({pars})";
    }
}
