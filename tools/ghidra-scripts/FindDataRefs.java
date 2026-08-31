//FindDataRefs.java
//@category Analysis
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import ghidra.program.model.symbol.ReferenceIterator;

public class FindDataRefs extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args == null || args.length == 0) {
            println("NO ARGS - expect hex VA list");
            return;
        }
        ReferenceManager refMgr = currentProgram.getReferenceManager();
        for (String vaStr : args) {
            String s = vaStr.trim();
            if (s.isEmpty()) continue;
            long addrVal = Long.parseLong(s.replace("0x","").replace("0X",""), 16);
            Address addr = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(addrVal);
            println("=== REFS TO " + s + " (" + String.format("0x%x", addrVal) + ") ===");
            ReferenceIterator it = refMgr.getReferencesTo(addr);
            int count = 0;
            for (Reference r : it) {
                Address from = r.getFromAddress();
                Function f = currentProgram.getFunctionManager().getFunctionContaining(from);
                String funcName = (f != null) ? f.getName() + " @ " + f.getEntryPoint() : "(no function) @ " + from;
                String refType = r.getReferenceType().toString();
                println("  " + from + " -> " + s + "  type=" + refType + "  func=" + funcName);
                count++;
                if (count > 200) {
                    println("  ... truncated at 200");
                    break;
                }
            }
            if (count == 0) println("  (no references via ReferenceManager)");
            else println("  total: " + count);
            try {
                byte b = currentProgram.getMemory().getByte(addr);
                println("  byte at target: " + String.format("%02x", b & 0xFF));
            } catch (Exception e) {
                println("  cannot read byte: " + e);
            }
        }
        println("DONE FindDataRefs");
    }
}
