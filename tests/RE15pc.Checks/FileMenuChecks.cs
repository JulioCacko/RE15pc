using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

static class FileMenuChecks
{
    public static void Run(Action<bool, string> check)
    {
        var memory = new PSMemory();
        var cpu = new CpuContext();
        var points = new (uint Address, short Start, short Step)[] {
            (0x800B25E0, 215, -15), (0x800B25E6, 82, -9),
            (0x800B25D8, 126, -7), (0x800B25DC, 150, -7),
            (0x800B25EA, 166, 7), (0x800B25F2, 26, -8)
        };
        foreach (var point in points) memory.WriteU16(point.Address, (ushort)point.Start);
        for (var i = 0; i < 26; i++) Recompiled.re15pc_main.func_8004C374(cpu, memory);
        check(points.All(p => (short)memory.ReadU16(p.Address) == p.Start + 25 * p.Step) &&
            memory.ReadU8(0x800B25C2) == 1, "original FILE handler slides panels out in 25 steps");
        for (var i = 0; i < 10; i++) Recompiled.re15pc_main.func_8004C374(cpu, memory);
        check(memory.ReadU8(0x800B25C2) == 1 && (short)memory.ReadU16(0x800B25E0) == -160,
            "original FILE handler waits after the slide");
        memory.WriteU32(0x800AC76C, 0x8000); // Mapped Cross trigger.
        Recompiled.re15pc_main.func_8004C374(cpu, memory);
        memory.WriteU32(0x800AC76C, 0);
        check(memory.ReadU8(0x800B25C2) == 2, "Cross advances FILE handler to its return animation");
        for (var i = 0; i < 26; i++) Recompiled.re15pc_main.func_8004C374(cpu, memory);
        check(points.All(p => (short)memory.ReadU16(p.Address) == p.Start) &&
            memory.ReadU8(0x800B25C1) == 0 && memory.ReadU8(0x800B25C2) == 0,
            "original FILE handler restores panel positions");
    }
}
