namespace PointerBridge_Client;

[Flags]
internal enum PenFlags : byte
{
    None = 0,
    InRange = 1,
    Contact = 2,
    Eraser = 4,
    BarrelDown = 8,
    BarrelKnown = 16
}

// 归一化到 ushort，既减少包大小，也在发送前消除低于量化精度的变化。
internal readonly record struct PenSample(ushort X, ushort Y, ushort Pressure, PenFlags Flags)
{
    public PenSample Released => this with
    {
        Pressure = 0,
        Flags = Flags & (PenFlags.Eraser | PenFlags.BarrelKnown)
    };

    public static ushort Quantize(double value) =>
        (ushort)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1) * ushort.MaxValue);
}
