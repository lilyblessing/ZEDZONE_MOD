using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

string interopDir = @"D:\SteamLibrary\steamapps\common\ZED ZONE\BepInEx\interop";
string asmPath = Path.Combine(interopDir, "Assembly-CSharp.dll");

using var fs = new FileStream(asmPath, FileMode.Open, FileAccess.Read);
using var pe = new PEReader(fs);
var mr = pe.GetMetadataReader();

var typeMap = new Dictionary<int, string>();
var methodMap = new Dictionary<int, string>();
var fieldMap = new Dictionary<int, string>();

foreach (var th in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(th);
    string typeName = mr.GetString(td.Name);
    string ns = mr.GetString(td.Namespace);
    string fullName = string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
    typeMap[MetadataTokens.GetToken(th) & 0xFFFFFF] = fullName;
    foreach (var fh in td.GetFields())
    {
        var fd = mr.GetFieldDefinition(fh);
        fieldMap[MetadataTokens.GetToken(fh) & 0xFFFFFF] = fullName + "." + mr.GetString(fd.Name);
    }
    foreach (var mh in td.GetMethods())
    {
        var md = mr.GetMethodDefinition(mh);
        methodMap[MetadataTokens.GetToken(mh) & 0xFFFFFF] = fullName + "::" + mr.GetString(md.Name);
    }
}

// === PART 1: All methods referencing craftTime ===
Console.WriteLine("=== ALL methods referencing craftTime ===");
foreach (var th in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(th);
    string tn = mr.GetString(td.Name);
    string ns = mr.GetString(td.Namespace);
    string full = string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
    foreach (var mh in td.GetMethods())
    {
        var md = mr.GetMethodDefinition(mh);
        string mn = mr.GetString(md.Name);
        try
        {
            var body = pe.GetMethodBody(md.RelativeVirtualAddress);
            var il = body?.GetILBytes();
            if (il == null) continue;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                byte op = il[i];
                if ((op == 0x7B || op == 0x7C || op == 0x7D || op == 0x7E || op == 0x7F) && i + 4 <= il.Length)
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    int r = tok & 0xFFFFFF;
                    if (fieldMap.TryGetValue(r, out var fn) && fn.Contains("craftTime"))
                    {
                        Console.WriteLine($"  {full}::{mn} -> {fn}");
                    }
                }
            }
        }
        catch { }
    }
}

// === PART 2: All methods calling GetBuildRealSeconds ===
Console.WriteLine("\n=== ALL methods calling GetBuildRealSeconds ===");
foreach (var th in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(th);
    string tn = mr.GetString(td.Name);
    string ns = mr.GetString(td.Namespace);
    string full = string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
    foreach (var mh in td.GetMethods())
    {
        var md = mr.GetMethodDefinition(mh);
        string mn = mr.GetString(md.Name);
        try
        {
            var body = pe.GetMethodBody(md.RelativeVirtualAddress);
            var il = body?.GetILBytes();
            if (il == null) continue;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                byte op = il[i];
                if ((op == 0x28 || op == 0x6F) && i + 4 <= il.Length)
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    int r = tok & 0xFFFFFF;
                    if (methodMap.TryGetValue(r, out var fn) && fn.Contains("GetBuildRealSeconds"))
                    {
                        Console.WriteLine($"  {full}::{mn} calls {fn}");
                    }
                }
            }
        }
        catch { }
    }
}

// === PART 3: All methods referencing statTimeValueText ===
Console.WriteLine("\n=== ALL methods referencing statTimeValueText ===");
foreach (var th in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(th);
    string tn = mr.GetString(td.Name);
    string ns = mr.GetString(td.Namespace);
    string full = string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
    foreach (var mh in td.GetMethods())
    {
        var md = mr.GetMethodDefinition(mh);
        string mn = mr.GetString(md.Name);
        try
        {
            var body = pe.GetMethodBody(md.RelativeVirtualAddress);
            var il = body?.GetILBytes();
            if (il == null) continue;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                byte op = il[i];
                if ((op == 0x7B || op == 0x7C || op == 0x7D || op == 0x7E || op == 0x7F) && i + 4 <= il.Length)
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    int r = tok & 0xFFFFFF;
                    if (fieldMap.TryGetValue(r, out var fn) && fn.Contains("statTimeValueText"))
                    {
                        Console.WriteLine($"  {full}::{mn} -> {fn}");
                    }
                }
            }
        }
        catch { }
    }
}

// === PART 4: All methods referencing totalTime ===
Console.WriteLine("\n=== ALL methods referencing totalTime ===");
foreach (var th in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(th);
    string tn = mr.GetString(td.Name);
    string ns = mr.GetString(td.Namespace);
    string full = string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
    foreach (var mh in td.GetMethods())
    {
        var md = mr.GetMethodDefinition(mh);
        string mn = mr.GetString(md.Name);
        try
        {
            var body = pe.GetMethodBody(md.RelativeVirtualAddress);
            var il = body?.GetILBytes();
            if (il == null) continue;
            for (int i = 0; i + 4 <= il.Length; i++)
            {
                byte op = il[i];
                if ((op == 0x7B || op == 0x7C || op == 0x7D || op == 0x7E || op == 0x7F) && i + 4 <= il.Length)
                {
                    int tok = BitConverter.ToInt32(il, i + 1);
                    int r = tok & 0xFFFFFF;
                    if (fieldMap.TryGetValue(r, out var fn) && fn.Contains("totalTime"))
                    {
                        Console.WriteLine($"  {full}::{mn} -> {fn}");
                    }
                }
            }
        }
        catch { }
    }
}

// === PART 5: DISASSEMBLE GetBuildRealSeconds ===
Console.WriteLine("\n=== DISASSEMBLE GetBuildRealSeconds ===");
DisassembleMethod("BuildInfoPanel", "GetBuildRealSeconds");
Console.WriteLine("\n=== DISASSEMBLE BuildTimeFormat ===");
DisassembleMethod("BuildInfoPanel", "BuildTimeFormat");
Console.WriteLine("\n=== DISASSEMBLE GetTotalMaterialNumber ===");
DisassembleMethod("BuildInfoPanel", "GetTotalMaterialNumber");
Console.WriteLine("\n=== DISASSEMBLE ShowTerrainObjectDetails ===");
DisassembleMethod("ConstructionPanel", "ShowTerrainObjectDetails");
Console.WriteLine("\n=== DISASSEMBLE SelectItem ===");
DisassembleMethod("ConstructionPanel", "SelectItem");
Console.WriteLine("\n=== DISASSEMBLE GetTitleString ===");
DisassembleMethod("BuildInfoPanel", "GetTitleString");

// === PART 6: Search for constants like 1440 or 86400 or 24 ===
Console.WriteLine("\n=== Float constants in GetBuildRealSeconds area ===");
DisassembleWithConstants("BuildInfoPanel", "GetBuildRealSeconds");

void DisassembleMethod(string typeName, string methodName)
{
    foreach (var th in mr.TypeDefinitions)
    {
        var td = mr.GetTypeDefinition(th);
        if (mr.GetString(td.Name) != typeName) continue;
        foreach (var mh in td.GetMethods())
        {
            var md = mr.GetMethodDefinition(mh);
            if (mr.GetString(md.Name) != methodName) continue;
            try
            {
                var body = pe.GetMethodBody(md.RelativeVirtualAddress);
                var il = body?.GetILBytes();
                if (il != null && il.Length > 0)
                {
                    Console.WriteLine($"  IL ({il.Length} bytes):");
                    int idx = 0, cnt = 0;
                    while (idx < il.Length && cnt < 300)
                    {
                        int off = idx;
                        byte op = il[idx++];
                        string name = OpName(op, il, ref idx, methodMap, fieldMap, typeMap, mr);
                        Console.WriteLine($"    {off:X4} {name}");
                        cnt++;
                    }
                }
                else Console.WriteLine("  (no IL body)");
            }
            catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); }
        }
    }
}

void DisassembleWithConstants(string typeName, string methodName)
{
    foreach (var th in mr.TypeDefinitions)
    {
        var td = mr.GetTypeDefinition(th);
        if (mr.GetString(td.Name) != typeName) continue;
        foreach (var mh in td.GetMethods())
        {
            var md = mr.GetMethodDefinition(mh);
            if (mr.GetString(md.Name) != methodName) continue;
            try
            {
                var body = pe.GetMethodBody(md.RelativeVirtualAddress);
                var il = body?.GetILBytes();
                if (il == null) continue;
                // Just scan for all ldarg/ldc/ldfld patterns
                int idx = 0;
                while (idx < il.Length)
                {
                    byte op = il[idx++];
                    if (op == 0x1A) { var v = BitConverter.ToSingle(il, idx); idx += 4; Console.WriteLine($"  CONST r4: {v}"); }
                    else if (op == 0x19) { var v = BitConverter.ToInt32(il, idx); idx += 4; Console.WriteLine($"  CONST i4: {v}"); }
                    else if (op == 0x02) Console.WriteLine($"  ldarg.0");
                    else if (op == 0x03) Console.WriteLine($"  ldarg.1");
                    else if (op == 0x7B) { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; Console.WriteLine($"  ldfld: {fieldMap.GetValueOrDefault(r, $"0x{r:X}")}"); }
                    else if (op == 0x28 || op == 0x6F) { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; Console.WriteLine($"  call: {methodMap.GetValueOrDefault(r, $"0x{r:X}")}"); }
                    else if (op == 0x18) idx++; // ldarg.s
                    else if (op == 0x09) idx++; // ldarg.s
                    else if (op == 0x22) idx++; // stloc.s
                    else if (op == 0x24) idx++; // ldloca.s
                    else if (op == 0x2B || op == 0x2C || op == 0x2D || op == 0x2E || op == 0x2F || op == 0x30 || op == 0x31 || op == 0x32 || op == 0x33) idx++; // branch.s
                    else if (op == 0x38 || op == 0x39 || op == 0x3A || op == 0x3B || op == 0x3C || op == 0x3D || op == 0x3E || op == 0x3F || op == 0x40) idx += 4; // branch
                    else if (op == 0x72) idx += 4; // ldstr
                    else if (op == 0x73 || op == 0x74 || op == 0x75 || op == 0x7C || op == 0x7D || op == 0x7E || op == 0x7F || op == 0x8C || op == 0x8D) idx += 4;
                    else if (op == 0xDD || op == 0xDE) idx += (op == 0xDD) ? 4 : 1;
                    else if (op == 0xFE) { byte op2 = il[idx++]; if (op2 == 0x05) { } } // clt.un
                }
            }
            catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); }
        }
    }
}

string OpName(byte op, byte[] il, ref int idx, Dictionary<int,string> mmap, Dictionary<int,string> fmap, Dictionary<int,string> tmap, MetadataReader reader)
{
    switch (op)
    {
        case 0x02: return "ldarg.0";
        case 0x03: return "ldarg.1";
        case 0x04: return "ldarg.2";
        case 0x05: return "ldarg.3";
        case 0x06: return "ldloc.0";
        case 0x07: return "ldloc.1";
        case 0x08: return "ldloc.2";
        case 0x09: idx++; return "ldarg.s";
        case 0x0A: idx++; return "ldarga.s";
        case 0x0B: idx++; return "starg.s";
        case 0x0C: idx++; return "starga.s";
        case 0x0D: return "ldnull";
        case 0x0E: return "ldc.i4.m1";
        case 0x0F: return "ldc.i4.0";
        case 0x10: return "ldc.i4.1";
        case 0x11: return "ldc.i4.2";
        case 0x12: return "ldc.i4.3";
        case 0x13: return "ldc.i4.4";
        case 0x14: return "ldc.i4.5";
        case 0x15: return "ldc.i4.6";
        case 0x16: return "ldc.i4.7";
        case 0x17: return "ldc.i4.8";
        case 0x18: { var v = (sbyte)il[idx++]; return $"ldc.i4.s {v}"; }
        case 0x19: { var v = BitConverter.ToInt32(il, idx); idx += 4; return $"ldc.i4 {v}"; }
        case 0x1A: { var v = BitConverter.ToSingle(il, idx); idx += 4; return $"ldc.r4 {v}"; }
        case 0x1B: { idx += 8; return "ldc.r8"; }
        case 0x1E: return "stloc.0";
        case 0x1F: return "stloc.1";
        case 0x20: return "stloc.2";
        case 0x21: return "stloc.3";
        case 0x22: idx++; return "stloc.s";
        case 0x23: return "ldloca.s 0";
        case 0x24: idx++; return "ldloca.s";
        case 0x25: return "dup";
        case 0x26: return "pop";
        case 0x2A: return "ret";
        case 0x2B: { var d = (sbyte)il[idx++]; return $"br.s IL_{idx + d:X4}"; }
        case 0x2C: { var d = (sbyte)il[idx++]; return $"brfalse.s IL_{idx + d:X4}"; }
        case 0x2D: { var d = (sbyte)il[idx++]; return $"brtrue.s IL_{idx + d:X4}"; }
        case 0x2E: { var d = (sbyte)il[idx++]; return $"beq.s IL_{idx + d:X4}"; }
        case 0x2F: { var d = (sbyte)il[idx++]; return $"bge.s IL_{idx + d:X4}"; }
        case 0x30: { var d = (sbyte)il[idx++]; return $"bgt.s IL_{idx + d:X4}"; }
        case 0x31: { var d = (sbyte)il[idx++]; return $"ble.s IL_{idx + d:X4}"; }
        case 0x32: { var d = (sbyte)il[idx++]; return $"blt.s IL_{idx + d:X4}"; }
        case 0x33: { var d = (sbyte)il[idx++]; return $"bne.un.s IL_{idx + d:X4}"; }
        case 0x38: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"br IL_{idx + d:X4}"; }
        case 0x39: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"brfalse IL_{idx + d:X4}"; }
        case 0x3A: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"brtrue IL_{idx + d:X4}"; }
        case 0x3B: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"beq IL_{idx + d:X4}"; }
        case 0x3C: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"bge IL_{idx + d:X4}"; }
        case 0x3D: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"bgt IL_{idx + d:X4}"; }
        case 0x3E: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"ble IL_{idx + d:X4}"; }
        case 0x3F: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"blt IL_{idx + d:X4}"; }
        case 0x40: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"bne.un IL_{idx + d:X4}"; }
        case 0x58: return "add";
        case 0x59: return "sub";
        case 0x5A: return "mul";
        case 0x5B: return "div";
        case 0x5C: return "and";
        case 0x5D: return "rem";
        case 0x5E: return "or";
        case 0x69: return "conv.i4";
        case 0x6A: return "conv.r4";
        case 0x6B: return "conv.r8";
        case 0x6C: return "conv.u4";
        case 0x6D: return "conv.u8";
        case 0x72:
        { int tok = BitConverter.ToInt32(il, idx); idx += 4;
          try { var h = MetadataTokens.UserStringHandle(tok & 0xFFFFFF); return $"ldstr \"{reader.GetUserString(h)}\""; }
          catch { return $"ldstr 0x{tok:X8}"; } }
        case 0x73: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return mmap.TryGetValue(r, out var m) ? $"newobj {m}" : $"newobj 0x{tok:X8}"; }
        case 0x74: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return tmap.TryGetValue(r, out var t) ? $"castclass {t}" : $"castclass 0x{tok:X8}"; }
        case 0x75: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return tmap.TryGetValue(r, out var t) ? $"isinst {t}" : $"isinst 0x{tok:X8}"; }
        case 0x7B: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return fmap.TryGetValue(r, out var f) ? $"ldfld {f}" : $"ldfld 0x{tok:X8}"; }
        case 0x7C: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return fmap.TryGetValue(r, out var f) ? $"ldflda {f}" : $"ldflda 0x{tok:X8}"; }
        case 0x7D: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return fmap.TryGetValue(r, out var f) ? $"stfld {f}" : $"stfld 0x{tok:X8}"; }
        case 0x7E: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return fmap.TryGetValue(r, out var f) ? $"ldsfld {f}" : $"ldsfld 0x{tok:X8}"; }
        case 0x7F: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return fmap.TryGetValue(r, out var f) ? $"stsfld {f}" : $"stsfld 0x{tok:X8}"; }
        case 0x8C: { int tok = BitConverter.ToInt32(il, idx); idx += 4; return $"box 0x{tok:X8}"; }
        case 0x8D: { int tok = BitConverter.ToInt32(il, idx); idx += 4; return $"newarr 0x{tok:X8}"; }
        case 0x9A: return "ldlen";
        case 0x9E: return "ldelem.i4";
        case 0xA2: return "stelem.i4";
        case 0xD0: { int tok = BitConverter.ToInt32(il, idx); idx += 4; int r = tok & 0xFFFFFF; return tmap.TryGetValue(r, out var t) ? $"ldtoken {t}" : $"ldtoken 0x{tok:X8}"; }
        case 0xDD: { var d = BitConverter.ToInt32(il, idx); idx += 4; return $"leave IL_{idx + d:X4}"; }
        case 0xDC: return "endfinally";
        case 0xDE: { var d = (sbyte)il[idx++]; return $"leave.s IL_{idx + d:X4}"; }
        case 0xFE:
        { byte op2 = il[idx++];
          if (op2 == 0x01) return "ceq";
          if (op2 == 0x02) return "cgt";
          if (op2 == 0x04) return "clt";
          if (op2 == 0x05) return "clt.un";
          return $"FE.{op2:X2}"; }
        default: return $"0x{op:X2}";
    }
}
