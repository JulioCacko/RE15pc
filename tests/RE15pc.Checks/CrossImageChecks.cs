using RecompOne.Recompiler.Analysis;
using RecompOne.Recompiler.Disasm;

static class CrossImageChecks
{
    public static void Run(Action<bool, string> check)
    {
        const uint origin = 0x80010000, target = origin + 8;
        var words = new uint[] { 0x03e00008, 0, 0x2402002a, 0x03e00008, 0, 0x03e00008, 0 };
        var body = words.Select((word, i) => new MipsInstruction(word, origin + (uint)i * 4)).ToArray();
        var calls = new uint[] { 0x0c000000 | ((target >> 2) & 0x03ffffff), 0, 0x03e00008, 0 }
            .Select((word, i) => new MipsInstruction(word, 0x80100000 + (uint)i * 4)).ToArray();
        try
        {
            var known = new List<MipsFunction> {
                new() { Start=origin, End=origin+8, Instructions=body[..2] },
                new() { Start=origin+20, End=origin+28, Instructions=body[5..] }
            };
            var source = new List<MipsFunction> { new() {
                Start=0x80100000, End=0x80100010, Instructions=calls
            } };
            var images = new[] { new ImageFunctions("main", known, body), new ImageFunctions("stage", source, calls) };
            FunctionPipeline.ScanCrossImage(images);
            var found = known.Where(f => f.Start == target).ToArray();
            check(found.Length == 1 && found[0].End == origin + 20 && found[0].Instructions.Length == 3,
                "cross-image call discovers code in a gap between mapped functions");
            FunctionPipeline.ScanCrossImage(images);
            check(known.Count(f => f.Start == target) == 1, "cross-image rescan does not duplicate the entry");
            using var invalid = new MipsInstruction(0xffffffff, target);
            var invalidBody = body.ToArray();
            invalidBody[2] = invalid;
            var invalidKnown = known.Where(f => f.Start != target).ToList();
            FunctionPipeline.ScanCrossImage([new("bad", invalidKnown, invalidBody), new("stage", source, calls)]);
            check(invalidKnown.All(f => f.Start != target), "cross-image targets with unsupported instructions are rejected");
        }
        finally
        {
            foreach (var instruction in body.Concat(calls)) instruction.Dispose();
        }
    }
}
