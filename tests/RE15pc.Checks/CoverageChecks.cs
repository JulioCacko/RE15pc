using RE15pc.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Memory;

static class CoverageChecks
{
    public static void Run(Action<bool, string> check)
    {
        ExecutionCoverage.Reset();
        ExecutionCoverage.Visit("main", 1);
        ExecutionCoverage.ReadSector(10, 2048);
        check(ExecutionCoverage.Snapshot().Functions.Length == 0 && ExecutionCoverage.Snapshot().Sectors.Length == 0,
            "disabled coverage records nothing");
        ExecutionCoverage.Enabled = true;
        ExecutionCoverage.Visit("stage1", 0x80100000);
        ExecutionCoverage.Visit("stage1", 0x80100000);
        ExecutionCoverage.Visit("title", 0x80100000);
        ExecutionCoverage.ReadSector(10, 1024);
        ExecutionCoverage.ReadSector(10, 1024);
        ExecutionCoverage.ReadSector(11, 9);
        ExecutionCoverage.ReadSector(-1, 2048);
        var first = ExecutionCoverage.Snapshot();
        check(first.Functions.Length == 2, "entry coverage separates overlays sharing an address");
        check(first.Sectors.Length == 2 && first.Sectors[0].Bytes == 1024,
            "repeated partial reads are not added into a false full read");
        var file = new DiscFile("example", 10, 2058);
        var partial = ContentCoverage.Measure(file, first.Sectors.ToDictionary(s => s.Lba, s => s.Bytes));
        check(partial.TouchedSectors == 2 && partial.CompleteSectors == 0 && !partial.ExtentRead,
            "touched sectors do not imply a complete file read");
        ExecutionCoverage.ReadSector(10, 2352);
        ExecutionCoverage.ReadSector(11, 10);
        var full = ContentCoverage.Measure(file,
            ExecutionCoverage.Snapshot().Sectors.ToDictionary(s => s.Lba, s => s.Bytes));
        check(full.ExtentRead && full.CompleteSectors == 2, "last sector needs only the file's remaining bytes");
        ExecutionCoverage.Reset();
        check(first.Functions.Length == 2 && first.Sectors[0].Bytes == 1024, "coverage snapshots are detached from subsequent mutation");

        // Exercise actual generated direct-call and SDK-replacement wrappers.
        ExecutionCoverage.Enabled = true;
        var memory = new PSMemory();
        var cpu = new CpuContext { A0 = 0x80001000 };
        memory.WriteU16(cpu.A0, 0x1234);
        Recompiled.re15pc_main.DecDCTBufSize(cpu, memory);
        check(cpu.V0 == 0x1234, "instrumented guest method preserves its result");
        Recompiled.re15pc_main.LoadImage(cpu, memory);
        check(cpu.V0 == 0xffffffff, "instrumented SDK replacement preserves its invalid-rectangle result");
        var actual = ExecutionCoverage.Snapshot().Functions;
        check(actual.Contains(new("main", 0x8006D780)) && actual.Contains(new("main", 0x80068C88)),
            "generated direct and replaced methods both produce entry evidence");
        ExecutionCoverage.Reset();
    }
}
