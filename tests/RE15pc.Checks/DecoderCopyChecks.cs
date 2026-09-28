using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

static class DecoderCopyChecks
{
    public static void Run(Action<bool, string> check)
    {
        Dispatcher.Reset();
        Dispatcher.Register("main", new Recompiled.MainDispatchTable());
        Dispatcher.Load("main");
        var memory = new PSMemory();
        const uint source = 0x80001000, destination = 0x80003000;
        // Start with the exact length that reached the formerly unmapped target.
        foreach (var count in new[] { 31 }.Concat(Enumerable.Range(0, 66)).Append(1024))
        {
            for (var i = 0; i < count + 1; i++)
            {
                memory.WriteU8(source + (uint)i, (byte)(i * 19 + 11));
                memory.WriteU8(destination + (uint)i, 0xee);
            }
            var cpu = new CpuContext { S2 = 32, S3 = 0x80010310, S4 = (uint)count,
                S5 = source, T3 = destination };
            Recompiled.re15pc_main.func_800102D8(cpu, memory);
            var equal = true;
            for (var i = 0; i < count; i++)
                equal &= memory.ReadU8(destination + (uint)i) == (byte)(i * 19 + 11);
            check(equal && memory.ReadU8(destination + (uint)count) == 0xee &&
                cpu.S5 == source + count && cpu.T3 == destination + count && cpu.S0 == 0,
                $"original computed-copy length {count}");
        }
        for (var i = 0; i <= 32; i++)
            check(Dispatcher.CanCall(0x80010310 + (uint)i * 12), $"computed-copy suffix {i} is dispatchable");
    }
}
