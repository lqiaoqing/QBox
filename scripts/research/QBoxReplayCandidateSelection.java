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
public class QBoxReplayCandidateSelection extends GhidraScript {
    private static final String BINARY_SHA = "f728593f86597b95d9ee79c03f8e7d313ca4c79a4bab8cf31f3768febeb8063f";
    private static final long START = 0x141fb7f20L, STACK = 0x6000fff8L, STOP = 0x70000000L;
    private static final long[][] RANGES = {
        {START,0x141fb8accL}, {0x141f8d210L,0x141f8d3c2L}, {0x141fb6fa0L,0x141fb7f20L},
        {0x141fb92b0L,0x141fb94e6L}, {0x141fbe120L,0x141fbe45eL}, {0x141fb6db0L,0x141fb6f9eL}
    };
    private static final String[] HASHES = {
        "329bfa8a46cf790985efb81c2d80747638dcd835aa792def85f1a5a70992b114",
        "8be5409bf7dbd68acaa1f4d84d25a03722f5e7481a6d54fb74c82668862a1936",
        "c16e3818655afb4f4f88f2dd95ab77ae80805a846f701a3b7ec554215585ba64",
        "90e1fdf8f25f7b692f4757913ce899e7c6187a1d840d0cdf2028b524de72b276",
        "69099065cf653bbee91ef178d714fa99ad3a4875055cca5f7952d8fe75f0900b",
        "87b46e61e80c15fbbc5c03be5e98dcbebdae87b41c6c3cfd485c900f9ac3720b"
    };
    private final Map<String,Integer> entries = new TreeMap<>();
    private String sha(byte[] bytes) throws Exception {
        return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes));
    }
    private void verifyCode() throws Exception {
        for (int i = 0; i < RANGES.length; i++) {
            long start = RANGES[i][0], end = RANGES[i][1];
            byte[] bytes = new byte[(int)(end - start)];
            currentProgram.getMemory().getBytes(toAddr(start),bytes);
            if (!sha(bytes).equals(HASHES[i])) throw new IllegalStateException("selected code hash mismatch");
            new DisassembleCommand(toAddr(start),new AddressSet(toAddr(start),toAddr(end-1)),true)
                .applyTo(currentProgram,monitor);
        }
    }
    private void put(EmulatorHelper emu, long address, long value) {
        emu.writeMemory(toAddr(address),ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).array());
    }
    private Map<String,Object> replay(int count, int limit, int mode, int seed) throws Exception {
        List<byte[]> records = new ArrayList<>();
        long[] keys = {0,1,0x108030390001e240L,0xda8030390001e240L,Long.MAX_VALUE,Long.MIN_VALUE,-1};
        int[] scores = {0,1,2,12345,Integer.MAX_VALUE,Integer.MIN_VALUE,-1};
        for (int i = 0; i < count; i++) {
            ByteBuffer record = ByteBuffer.allocate(24).order(ByteOrder.LITTLE_ENDIAN);
            record.putInt(i+1).putShort((short)((i*7+seed)%4)).putShort((short)0);
            record.putLong(keys[(i+seed)%keys.length]).putInt(scores[(i*3+seed)%scores.length]).putInt(0x51a71e);
            records.add(record.array());
        }
        if (seed == 1) Collections.reverse(records);
        if (seed == 2) Collections.shuffle(records,new Random(0x51a7L + count));
        byte[] input = new byte[count * 24];
        for (int i = 0; i < count; i++) System.arraycopy(records.get(i),0,input,i*24,24);
        EmulatorHelper emu = new EmulatorHelper(currentProgram);
        try {
            emu.writeMemory(toAddr(STACK-65536),new byte[131072]);
            for (String register : new String[]{"RAX","RBX","RCX","RDX","RBP","RSI","RDI",
                    "R8","R9","R10","R11","R12","R13","R14","R15"}) emu.writeRegister(register,0);
            long array = 0x50004000L, context = 0x50000000L;
            byte[] guard = new byte[16]; Arrays.fill(guard,(byte)0xa5);
            emu.writeMemory(toAddr(array-16),guard); emu.writeMemory(toAddr(array+input.length),guard);
            // 整页写入可能保留传入数组；使用副本，保证后续模拟写入不会改写 oracle 的原始输入。
            emu.writeMemory(toAddr(array),input.clone());
            if (!Arrays.equals(input,emu.readMemory(toAddr(array),input.length))) throw new IllegalStateException("input readback mismatch");
            emu.writeMemory(toAddr(0x50008000L),new byte[128]);
            put(emu,context,context+16); put(emu,context+16,mode);
            put(emu,STACK,STOP); put(emu,STACK+0x28,context);
            emu.writeRegister("RSP",STACK); emu.writeRegister("RCX",0x50008000L);
            emu.writeRegister("RDX",array); emu.writeRegister("R8",count); emu.writeRegister("R9",limit);
            emu.writeRegister("RIP",START);
            boolean done = false;
            for (int steps = 0; steps < 1000000; steps++) {
                long pc = emu.getExecutionAddress().getOffset();
                if (pc == STOP) { done = true; break; }
                boolean allowed = false;
                for (long[] range : RANGES) {
                    if (pc == range[0]) entries.merge(Long.toHexString(pc),1,Integer::sum);
                    if (pc >= range[0] && pc < range[1]) allowed = true;
                }
                // 所有比较、枢轴选择和递归均执行原始指令；不替代辅助函数或越界异常分支。
                if (!allowed) throw new IllegalStateException("execution left selected code: " + Long.toHexString(pc));
                if (!emu.step(monitor)) throw new IllegalStateException(emu.getLastError());
            }
            if (!done) throw new IllegalStateException("instruction bound reached");
            if (!Arrays.equals(guard,emu.readMemory(toAddr(array-16),16)) ||
                    !Arrays.equals(guard,emu.readMemory(toAddr(array+input.length),16)))
                throw new IllegalStateException("array guard changed");
            byte[] output = emu.readMemory(toAddr(array),input.length);
            return Map.of("count",count,"limit",limit,"mode",mode,"seed",seed,
                "inputHex",HexFormat.of().formatHex(input),
                "outputHex",HexFormat.of().formatHex(output),"guardsUnchanged",true);
        } finally { emu.dispose(); }
    }
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 2 || !sha(Files.readAllBytes(Path.of(args[0]))).equals(BINARY_SHA))
            throw new IllegalArgumentException("binary SHA-256 mismatch or missing arguments");
        verifyCode();
        List<Object> cases = new ArrayList<>();
        for (int count : new int[]{1,2,3,8,16,17,51,64,127,256}) {
            Set<Integer> limits = new TreeSet<>(Arrays.asList(0,Math.min(1,count-1),count/2,count-1,Math.min(50,count-1)));
            for (int limit : limits) for (int mode : new int[]{0,1,2}) for (int seed = 0; seed < 3; seed++)
                cases.add(replay(count,limit,mode,seed));
        }
        Map<String,Object> output = new LinkedHashMap<>();
        output.put("binarySha256",BINARY_SHA); output.put("cases",cases); output.put("executedFunctionEntries",entries);
        output.put("scope","Original candidate selector and helper instructions; synthetic 24-byte hits; no helper replacements; excludes complete query execution and invalid index panic paths");
        Files.writeString(Path.of(args[1]),new GsonBuilder().setPrettyPrinting().create().toJson(output));
        println("candidate selection instruction cases=" + cases.size());
    }
}
