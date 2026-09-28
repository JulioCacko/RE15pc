using RecompOne.Runtime;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Interp;

static class GpuReadbackChecks
{
    public static void Run(Action<bool, string> check)
    {
        var inner = new MemoryGpu();
        var backend = new InterpBackend(inner);
        GpuHle.Backend = backend;
        GpuHle.Active = true;
        var gpu = new Gpu();

        void Upload(uint packed)
        {
            gpu.WriteGp0(0xA0000000);
            gpu.WriteGp0(0);
            gpu.WriteGp0(0x00010002); // Two pixels, one row.
            gpu.WriteGp0(packed);
        }
        uint Read()
        {
            gpu.WriteGp0(0xC0000000);
            gpu.WriteGp0(0);
            gpu.WriteGp0(0x00010002);
            return gpu.ReadData();
        }

        Upload(0x12345678);
        check(Read() == 0x12345678, "GPU reads an upload made before the next VSync");
        check(inner.Writes == 1, "readback consumes the pending upload once");
        backend.Publish();
        if (backend.Acquire()) backend.Compose(0);
        check(inner.Writes == 1, "publishing after readback cannot replay the consumed upload");

        Upload(0x11112222);
        backend.Publish();
        Upload(0x33334444);
        backend.Publish();
        Upload(0x55556666);
        check(Read() == 0x55556666, "readback orders queued frames before current recording");
        check(inner.Writes == 4, "all pending writes execute once in order");

        Upload(0x77778888);
        backend.Publish();
        check(backend.Acquire(), "published frame available");
        backend.Compose(0);
        check(Read() == 0x77778888 && inner.Writes == 5,
            "readback does not replay an already composed frame");
        GpuHle.Active = false;
        GpuHle.Backend = null;
    }

    private sealed class MemoryGpu : IGpuBackend
    {
        private readonly ushort[] _vram = new ushort[1024 * 512];
        public int Writes { get; private set; }
        public bool Ready => true;
        public void WriteVram(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
        {
            Writes++;
            for (var row = 0; row < h; row++) px.Slice(row * w, w).CopyTo(_vram.AsSpan((y + row) * 1024 + x, w));
        }
        public void ReadVram(int x, int y, int w, int h, Span<ushort> px)
        {
            for (var row = 0; row < h; row++) _vram.AsSpan((y + row) * 1024 + x, w).CopyTo(px.Slice(row * w, w));
        }
        public void SetDrawEnv(in HleDrawEnv env) { }
        public void Flush() { }
        public void Present(in HleDispEnv disp) { }
        public int RegisterImage(ReadOnlySpan<byte> rgba, int width, int height) => throw new NotSupportedException();
        public void DrawTri(in HleVertex a, in HleVertex b, in HleVertex c, in PrimFlags f) => throw new NotSupportedException();
        public void DrawRect(in HleRect r, in PrimFlags f) => throw new NotSupportedException();
        public void DrawLine(in HleVertex a, in HleVertex b, in PrimFlags f) => throw new NotSupportedException();
        public void FillRect(int x, int y, int w, int h, ushort color15) => throw new NotSupportedException();
        public void CopyVram(int sx, int sy, int dx, int dy, int w, int h) => throw new NotSupportedException();
    }
}
