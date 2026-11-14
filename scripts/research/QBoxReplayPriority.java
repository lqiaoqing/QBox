import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.*;
import com.google.gson.GsonBuilder;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.emulator.EmulatorHelper;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.AddressSet;

@SuppressWarnings("removal")
public class QBoxReplayPriority extends GhidraScript {
    private static final String BINARY_SHA = "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f";
    private static final long KEY_START = 0x141fc1000L, KEY_END = 0x141fc1433L;
    private static final long MERGE_START = 0x141b9c94bL, MERGE_END = 0x141b9c9cbL;
    private static final long STACK = 0x6000fff8L, STOP = 0x70000000L;
    private EmulatorHelper emu;

    private String sha(byte[] bytes) throws Exception {
        return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
    }
    private void verifyCode(long start, long end, String digest) throws Exception {
        byte[] bytes = new byte[(int)(end - start)];
        currentProgram.getMemory().getBytes(toAddr(start), bytes);
        if (!sha(bytes).equals(digest)) throw new IllegalStateException("Ghidra selected bytes mismatch");
        new DisassembleCommand(toAddr(start), new AddressSet(toAddr(start), toAddr(end - 1)), true)
            .applyTo(currentProgram, monitor);
    }
    private void put(long address, long value, int length) {
        byte[] bytes = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).array();
        emu.writeMemory(toAddr(address), Arrays.copyOf(bytes, length));
    }
    private void initialize() {
        emu = new EmulatorHelper(currentProgram);
        emu.writeMemory(toAddr(STACK - 65536), new byte[131072]);
        for (String register : new String[]{"RAX","RBX","RCX","RDX","RBP","RSI","RDI",
                "R8","R9","R10","R11","R12","R13","R14","R15"}) emu.writeRegister(register, 0);
        emu.writeRegister("RSP", STACK);
        put(STACK, STOP, 8);
    }
    private void execute(long start, long stop) throws Exception {
        emu.writeRegister("RIP", start);
        for (int step = 0; step < 50000; step++) {
            long pc = emu.getExecutionAddress().getOffset();
            if (pc == stop) return;
            long allowedEnd = start == KEY_START ? KEY_END : MERGE_END;
            if (pc < start || pc >= allowedEnd) throw new IllegalStateException("execution left selected code range");
            // 两段计算都没有外部调用；不替代分支、比较、移位或祖先遍历。
            if (!emu.step(monitor)) throw new IllegalStateException(emu.getLastError());
        }
        throw new IllegalStateException("instruction bound reached");
    }
    private Map<String,Object> merge(int inherited, int folder, int attribute, int name) throws Exception {
        initialize();
        try {
            long frame = STACK - 0x800;
            put(frame - 0x10, folder, 1);
            put(frame - 0x18, attribute, 1);
            put(frame - 0x30, name == 255 ? 2 : 1, 8);
            put(frame - 0x38, name == 255 ? 0 : name, 1);
            emu.writeRegister("RBP", frame);
            emu.writeRegister("R14", inherited);
            emu.writeRegister("R15", inherited);
            execute(MERGE_START, MERGE_END);
            Map<String,Object> row = new LinkedHashMap<>();
            row.put("inherited", inherited); row.put("folder", folder);
            row.put("attribute", attribute); row.put("name", name);
            row.put("result", emu.readRegister("R10").intValue() & 255);
            return row;
        } finally { emu.dispose(); }
    }
    private Map<String,Object> key(int priority, boolean directory, boolean suffix, int score,
            long tail, int parent, int[][] scopes, boolean disabled) throws Exception {
        initialize();
        try {
            long query = 0x50000000L, state = 0x50001000L, pointers = 0x50002000L;
            long nodes = 0x50003000L, heapScopes = 0x50006000L;
            emu.writeMemory(toAddr(query), new byte[0x200]);
            emu.writeMemory(toAddr(state), new byte[0x100]);
            emu.writeMemory(toAddr(pointers), new byte[16 * 8]);
            emu.writeMemory(toAddr(nodes), new byte[16 * 16]);
            put(state + 0x48, pointers, 8); put(state + 0x50, 16, 8);
            // 人工树 0 为根，1..15 依次嵌套；只提供读取夹具，不模拟祖先判定。
            for (int i = 0; i < 16; i++) {
                put(pointers + i * 8, nodes + i * 16, 8);
                put(nodes + i * 16, i == 0 ? -1 : i - 1, 4);
            }
            put(query + 0xc8, disabled ? 2 : 0, 4);
            put(query + 0xf0, scopes.length, 8);
            long scopeBase = query + 0xcc;
            if (scopes.length >= 5) {
                scopeBase = heapScopes;
                emu.writeMemory(toAddr(scopeBase), new byte[scopes.length * 8]);
                put(query + 0xd0, scopes.length, 8); put(query + 0xd8, scopeBase, 8);
            }
            for (int i = 0; i < scopes.length; i++) {
                put(scopeBase + i * 8, scopes[i][0], 4);
                put(scopeBase + i * 8 + 4, scopes[i][1], 2);
            }
            emu.writeRegister("RCX", 7); emu.writeRegister("RDX", state);
            emu.writeRegister("R8", query); emu.writeRegister("R9", parent & 0xffffffffL);
            put(STACK + 0x28, directory ? 1 : 0, 1); put(STACK + 0x30, suffix ? 1 : 0, 1);
            put(STACK + 0x38, priority, 1); put(STACK + 0x40, score, 4); put(STACK + 0x48, tail, 8);
            execute(KEY_START, STOP);
            Map<String,Object> row = new LinkedHashMap<>();
            row.put("priority", priority); row.put("directory", directory); row.put("suffix", suffix);
            row.put("score", score); row.put("tail", Long.toUnsignedString(tail));
            row.put("parent", parent); row.put("volume", 7); row.put("scopes", scopes);
            row.put("contextDisabled", disabled);
            row.put("key", Long.toUnsignedString(emu.readRegister("RAX").longValue()));
            return row;
        } finally { emu.dispose(); }
    }
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 2 || !sha(Files.readAllBytes(Path.of(args[0]))).equals(BINARY_SHA))
            throw new IllegalArgumentException("binary SHA-256 mismatch or missing arguments");
        verifyCode(KEY_START, KEY_END, "12894bcc289fdbd29b1170e82d1f16d26d1a0a2a1429be8342d79418d693d7b4");
        verifyCode(MERGE_START, MERGE_END, "a598720a737ff244ba5d59aadb8bed368d5bdd67932fbd84ef84b70daacefaa0");
        List<Map<String,Object>> merges = new ArrayList<>(), keys = new ArrayList<>();
        int[] priorities = {0, 1, 2, 4}, optional = {255, 0, 1, 2, 4};
        for (int inherited : priorities) for (int folder : optional)
            for (int attribute : optional) for (int name : optional)
                merges.add(merge(inherited, folder, attribute, name));
        println("merge instruction cases=" + merges.size());
        int[] scores = {Integer.MIN_VALUE,-8388609,-8388608,-1,0,1,8388607,8388608,Integer.MAX_VALUE};
        for (int priority : new int[]{1,2,4}) for (boolean directory : new boolean[]{false,true})
            for (boolean suffix : new boolean[]{false,true}) for (int score : scores)
                for (long tail : new long[]{0,1,0xffffffffL,0x100000000L,-1L})
                    keys.add(key(priority,directory,suffix,score,tail,5,new int[0][0],true));
        int[][][] contexts = { {}, {{5,7}}, {{4,7}}, {{1,7}}, {{0,7}}, {{15,7}}, {{5,8}},
            {{0,7},{1,7},{4,7},{5,7}}, {{5,8},{15,7},{0,7},{4,7},{5,7}},
            {{5,7},{4,7},{1,7},{0,7},{15,7},{5,8}} };
        for (int priority : new int[]{1,2,4}) for (int parent : new int[]{0,5,15,-1})
            for (int[][] scopes : contexts) for (boolean disabled : new boolean[]{false,true})
                keys.add(key(priority,false,false,12345,123456,parent,scopes,disabled));
        Map<String,Object> output = new LinkedHashMap<>();
        output.put("binarySha256", BINARY_SHA);
        output.put("scope", "Original selected P-code instructions; synthetic memory only; no external helper replacements; no Listary process execution");
        output.put("mergeRange", "0x141b9c94b..0x141b9c9cb (stop excluded)");
        output.put("keyRange", "0x141fc1000..0x141fc1433 (end excluded)");
        output.put("mergeCases", merges); output.put("keyCases", keys);
        Files.writeString(Path.of(args[1]), new GsonBuilder().setPrettyPrinting().create().toJson(output));
        println("priority key instruction cases=" + keys.size());
    }
}
