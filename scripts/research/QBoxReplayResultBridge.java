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
public class QBoxReplayResultBridge extends GhidraScript {
    private static final String BINARY_SHA = "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f";
    private static final long WIRE_START = 0x1416dd460L, WIRE_END = 0x1416dd4f5L;
    private static final long ROW_START = 0x14154bbfeL, ROW_END = 0x14154bc82L;
    private static final long ITEM_START = 0x14154b9c1L, ITEM_END = 0x14154ba00L;
    private static final long STACK = 0x6000fff8L, FRAME = 0x60008000L, STOP = 0x70000000L;
    private EmulatorHelper emu;

    private String sha(byte[] bytes) throws Exception {
        return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
    }
    private void verifyCode(long start, long end, String digest) throws Exception {
        byte[] bytes = new byte[(int)(end - start)];
        currentProgram.getMemory().getBytes(toAddr(start), bytes);
        if (!sha(bytes).equals(digest)) throw new IllegalStateException("selected code hash mismatch");
        new DisassembleCommand(toAddr(start), new AddressSet(toAddr(start), toAddr(end - 1)), true)
            .applyTo(currentProgram, monitor);
    }
    private void put(long address, long value, int size) {
        byte[] bytes = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).array();
        emu.writeMemory(toAddr(address), Arrays.copyOf(bytes, size));
    }
    private byte[] read(long address, int size) { return emu.readMemory(toAddr(address), size); }
    private void execute(long start, long end, long stop) throws Exception {
        emu.writeRegister("RIP", start);
        for (int step = 0; step < 1000; step++) {
            long pc = emu.getExecutionAddress().getOffset();
            if (pc == stop) return;
            // 三段均不含外部调用；离开选定区间立即失败，不替代字符串克隆或分配器。
            if (pc < start || pc >= end) throw new IllegalStateException("execution left selected range");
            if (!emu.step(monitor)) throw new IllegalStateException(emu.getLastError());
        }
        throw new IllegalStateException("instruction bound reached");
    }
    private Map<String,Object> replay(int keyIndex, int tailIndex, int directory, long key, long tail)
            throws Exception {
        emu = new EmulatorHelper(currentProgram);
        try {
            emu.writeMemory(toAddr(STACK - 65536), new byte[131072]);
            for (String register : new String[]{"RAX","RBX","RCX","RDX","RBP","RSI","RDI",
                    "R8","R9","R10","R11","R12","R13","R14","R15"}) emu.writeRegister(register, 0);
            emu.writeRegister("RSP", STACK); put(STACK, STOP, 8);
            long source = 0x50000000L, wire = 0x50001000L, rows = 0x50002000L;
            byte[] nativeBytes = new byte[0x78];
            for (int i = 0; i < nativeBytes.length; i++) nativeBytes[i] = (byte)(i + keyIndex * 13 + tailIndex);
            emu.writeMemory(toAddr(source), nativeBytes);
            emu.writeMemory(toAddr(wire), new byte[0x70]);
            emu.writeMemory(toAddr(rows), new byte[0x58 * 4]);
            long[] scores = {0,1,2,12345,0xffffffL,0x7fffffffL,0x80000000L,0xffffffffL};
            put(source + 0x58, tail, 8); put(source + 0x60, key, 8);
            put(source + 0x6c, scores[keyIndex], 4);
            for (int i = 0; i < 3; i++) put(source + 0x70 + i, (keyIndex >> i) & 1, 1);
            nativeBytes = read(source, 0x78);
            emu.writeRegister("RCX", wire); emu.writeRegister("RDX", source);
            execute(WIRE_START, WIRE_END, STOP);
            byte[] wireBytes = read(wire, 0x70);

            int slot = (keyIndex + tailIndex) % 4;
            emu.writeRegister("RBP", FRAME); emu.writeRegister("RDI", wire);
            emu.writeRegister("RSI", directory); emu.writeRegister("R15", rows); emu.writeRegister("R10", slot);
            // 仅为标量复制区间准备克隆结果描述符；不宣称验证字符串内容、所有权或整个转换循环。
            emu.writeMemory(toAddr(FRAME - 8), Arrays.copyOfRange(wireBytes, 0x10, 0x28));
            emu.writeMemory(toAddr(FRAME - 0x58), Arrays.copyOfRange(wireBytes, 0x28, 0x40));
            put(FRAME + 0x10, slot, 8);
            execute(ROW_START, ROW_END, ROW_END);
            long row = rows + slot * 0x58;
            byte[] rowBytes = read(row, 0x58);
            byte[] allRows = read(rows, 0x58 * 4);
            for (int i = 0; i < allRows.length; i++)
                if ((i < slot * 0x58 || i >= (slot + 1) * 0x58) && allRows[i] != 0)
                    throw new IllegalStateException("unselected destination row changed");

            emu.writeMemory(toAddr(FRAME), new byte[0x200]);
            emu.writeRegister("RBP", FRAME); emu.writeRegister("R14", row);
            execute(ITEM_START, ITEM_END, ITEM_END);
            return Map.of("keyIndex",keyIndex,"tailIndex",tailIndex,"directory",directory,"destinationSlot",slot,
                "nativeHex",HexFormat.of().formatHex(nativeBytes),"wireHex",HexFormat.of().formatHex(wireBytes),
                "rowHex",HexFormat.of().formatHex(rowBytes),"itemProjectionHex",HexFormat.of().formatHex(read(FRAME + 0x60,0xb8)),
                "otherRowsUnchanged",true);
        } finally { emu.dispose(); }
    }
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 2 || !sha(Files.readAllBytes(Path.of(args[0]))).equals(BINARY_SHA))
            throw new IllegalArgumentException("binary SHA-256 mismatch or missing arguments");
        verifyCode(WIRE_START,WIRE_END,"fb61703f4611da7638bb9d1583b074ba7a6d8dcb3827a4bd75c33845a4f8d7c7");
        verifyCode(ROW_START,ROW_END,"25419d671bb8744740b8b42f234a446710a21eb3a49662e32c7aff7c94c4a4c3");
        verifyCode(ITEM_START,ITEM_END,"a6945820c862527967b25ce183bbc627ac299c2adf654def424ea13764b8d326");
        long[] keys = {0,1,0x108030390001e240L,0xda8030390001e240L,Long.MAX_VALUE,Long.MIN_VALUE,-2,-1};
        long[] tails = {0,1,0xffffffffL,0x100000000L,Long.divideUnsigned(-1L,1000),
            Long.divideUnsigned(-1L,1000)+1,Long.MIN_VALUE,-1};
        List<Object> cases = new ArrayList<>();
        for (int k = 0; k < keys.length; k++) for (int t = 0; t < tails.length; t++)
            for (int directory = 0; directory < 2; directory++) cases.add(replay(k,t,directory,keys[k],tails[t]));
        Map<String,Object> output = new LinkedHashMap<>();
        output.put("binarySha256",BINARY_SHA); output.put("cases",cases);
        output.put("scope","Original wire converter and selected scalar-copy blocks; synthetic clone descriptors; no external helper replacements; not a full launcher execution");
        Files.writeString(Path.of(args[1]),new GsonBuilder().setPrettyPrinting().create().toJson(output));
        println("result bridge instruction cases=" + cases.size());
    }
}
