//DecompileVAs.java
//@category Analysis
// 用途：headless 模式下对指定 VA 批量反编译伪C，输出到 D:/tools/_ghidra_proj/decompiled_vas.c
// 用法（analyzeHeadless -postScript DecompileVAs.java 0x180930ab0 0x1809bc520 ...）
// 注意：Ghidra 12 兼容写法；脚本参数 = VA（hex 字符串，可带 0x 前缀）
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

import java.io.FileWriter;
import java.util.ArrayList;
import java.util.List;

public class DecompileVAs extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        List<String> vas = new ArrayList<String>();
        for (String a : args) {
            if (a != null && !a.trim().isEmpty()) vas.add(a.trim());
        }
        if (vas.isEmpty()) {
            println("NO VA ARGS GIVEN");
            return;
        }
        String outPath = System.getenv("DECOMP_OUT");
        if (outPath == null || outPath.isEmpty()) outPath = "D:/tools/_ghidra_proj/decompiled_vas.c";
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);
        ConsoleTaskMonitor monitor = new ConsoleTaskMonitor();
        FileWriter w = new FileWriter(outPath);
        try {
            for (String va : vas) {
                long addrVal = Long.parseLong(va.replace("0x", "").replace("0X", ""), 16);
                ghidra.program.model.address.Address a =
                    currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(addrVal);
                Function f = currentProgram.getFunctionManager().getFunctionAt(a);
                if (f == null) {
                    w.write("// NO FUNCTION at " + va + "\n");
                    println("NO FUNCTION at " + va);
                    continue;
                }
                DecompileResults res = decomp.decompileFunction(f, 120, monitor);
                w.write("// ==== " + f.getName() + " @ " + va + " ====\n");
                if (res != null && res.getDecompiledFunction() != null) {
                    w.write(res.getDecompiledFunction().getC() + "\n");
                    println("DECOMPILED " + f.getName() + " @ " + va);
                } else {
                    w.write("// DECOMPILE FAILED\n");
                    println("DECOMPILE FAILED @ " + va);
                }
            }
        } finally {
            w.close();
        }
        println("DONE -> " + outPath);
    }
}