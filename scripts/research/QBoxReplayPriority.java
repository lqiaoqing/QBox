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
    private static final long SETTING_START = 0x1405e7c40L, SETTING_END = 0x1405e7cc0L;
    private static final long CODEC_START = 0x1416dda10L, CODEC_END = 0x1416ddde6L;
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
    private long executeUntil(long start, long allowedEnd, long... stops) throws Exception {
        emu.writeRegister("RIP", start);
        for (int step = 0; step < 50000; step++) {
            long pc = emu.getExecutionAddress().getOffset();
            for (long stop : stops) if (pc == stop) return pc;
            if (pc < start || pc >= allowedEnd) throw new IllegalStateException("execution left selected code range");
            // 只执行选定原始指令；不替代分支、比较、移位或祖先遍历。
            if (!emu.step(monitor)) throw new IllegalStateException(emu.getLastError());
        }
        throw new IllegalStateException("instruction bound reached");
    }
    private void execute(long start, long stop) throws Exception {
        executeUntil(start, start == KEY_START ? KEY_END : MERGE_END, stop);
    }
    private long get(long address) {
        return ByteBuffer.wrap(emu.readMemory(toAddr(address), 8)).order(ByteOrder.LITTLE_ENDIAN).getLong();
    }
    private Map<String,Object> setting(int input) throws Exception {
        initialize();
        try {
            long output = 0x50000000L;
            emu.writeMemory(toAddr(output), new byte[64]);
            emu.writeRegister("RCX", output); emu.writeRegister("RDX", input);
            // 非法输入停在错误格式化入口；是否进入此分支完全由原始比较指令决定。
            long stopped = executeUntil(SETTING_START, SETTING_END, STOP, 0x1405e7c8aL);
            boolean accepted = stopped == STOP;
            if (accepted && get(output) != -1) throw new IllegalStateException("unexpected result tag");
            Map<String,Object> row = new LinkedHashMap<>();
            row.put("settingByte", input); row.put("signedSetting", (int)(byte)input);
            row.put("accepted", accepted);
            if (accepted) row.put("wireTier", emu.readMemory(toAddr(output + 8), 1)[0] & 255);
            return row;
        } finally { emu.dispose(); }
    }
    private Map<String,Object> codec(int length, int seed) throws Exception {
        initialize();
        try {
            long source = 0x50000000L, output = 0x50001000L, suffixes = 0x50002000L;
            emu.writeMemory(toAddr(source), new byte[0x60]);
            emu.writeMemory(toAddr(output), new byte[0x78]);
            emu.writeMemory(toAddr(suffixes), new byte[24]);
            put(suffixes, 9, 8); put(suffixes + 8, 0x50017000L, 8); put(suffixes + 16, 9, 8);
            int[] strides = {32,8,32,32}, offsets = {24,4,26,24};
            List<Object> vectors = new ArrayList<>();
            byte[][] originals = new byte[4][];
            for (int vector = 0; vector < 4; vector++) {
                long array = 0x50003000L + vector * 0x1000;
                byte[] bytes = new byte[length * strides[vector]];
                for (int i = 0; i < bytes.length; i++) bytes[i] = (byte)(0x50 + i % 32);
                for (int i = 0; i < length; i++) bytes[i * strides[vector] + offsets[vector]] = (byte)((seed + vector + i) % 4);
                originals[vector] = bytes.clone();
                if (bytes.length != 0) emu.writeMemory(toAddr(array), bytes);
                put(source + vector * 24, length, 8); put(source + vector * 24 + 8, array, 8);
                put(source + vector * 24 + 16, length, 8);
            }
            emu.writeRegister("RCX", output); emu.writeRegister("RDX", source); emu.writeRegister("R8", suffixes);
            executeUntil(CODEC_START, CODEC_END, STOP);
            for (int vector = 0; vector < 4; vector++) {
                long array = 0x50003000L + vector * 0x1000;
                byte[] after = length == 0 ? new byte[0] : emu.readMemory(toAddr(array), length * strides[vector]);
                List<Integer> input = new ArrayList<>(), result = new ArrayList<>();
                for (int i = 0; i < after.length; i++) {
                    if (i % strides[vector] == offsets[vector]) {
                        input.add(originals[vector][i] & 255); result.add(after[i] & 255);
                    } else if (after[i] != originals[vector][i]) throw new IllegalStateException("unrelated field changed");
                }
                if (get(output + vector * 24) != length || get(output + vector * 24 + 8) != array ||
                        get(output + vector * 24 + 16) != length) throw new IllegalStateException("vector topology changed");
                vectors.add(Map.of("vector",vector,"input",input,"result",result));
            }
            if (!Arrays.equals(emu.readMemory(toAddr(suffixes),24), emu.readMemory(toAddr(output + 0x60),24)))
                throw new IllegalStateException("suffix vector changed");
            return Map.of("length",length,"seed",seed,"vectors",vectors,"unrelatedFieldsUnchanged",true);
        } finally { emu.dispose(); }
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
        if ((args.length != 2 && !(args.length == 3 && args[2].equals("codec"))) ||
                !sha(Files.readAllBytes(Path.of(args[0]))).equals(BINARY_SHA))
            throw new IllegalArgumentException("binary SHA-256 mismatch or missing arguments");
        if (args.length == 3) {
            verifyCode(SETTING_START, SETTING_END, "a057ef61d0ab0e721f9b46fd88023047170e417c7b7dc9a9c5b907b9af76051c");
            verifyCode(CODEC_START, CODEC_END, "56f4841ff93c4b7981df6b2628a7998e32e4fecb7c032d8dc1128ced88066c98");
            List<Object> settings = new ArrayList<>(), codecs = new ArrayList<>();
            for (int input = 0; input < 256; input++) settings.add(setting(input));
            for (int length : new int[]{0,1,2,3,4,5,7,8,9,11}) for (int seed = 0; seed < 4; seed++)
                codecs.add(codec(length,seed));
            Map<String,Object> output = new LinkedHashMap<>();
            output.put("binarySha256",BINARY_SHA); output.put("settingCases",settings); output.put("codecCases",codecs);
            output.put("scope","Original selected instructions; rejected settings stop before error formatting; synthetic vectors only; no external helper replacements");
            Files.writeString(Path.of(args[1]),new GsonBuilder().setPrettyPrinting().create().toJson(output));
            println("setting instruction cases=256; vector codec instruction cases=40");
            return;
        }
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
