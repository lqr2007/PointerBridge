using System.Buffers.Binary;

namespace PointerBridge_Server;

internal readonly record struct PenPacket(byte Flags, uint Session, uint Sequence,
    ushort X, ushort Y, ushort Pressure)
{
    public bool InRange => (Flags & 1) != 0;
    public bool Contact => (Flags & 2) != 0;
    public bool Eraser => (Flags & 4) != 0;
    public bool BarrelDown => (Flags & 8) != 0;
    public bool BarrelKnown => (Flags & 16) != 0;

    public static bool TryParse(ReadOnlySpan<byte> data, out PenPacket packet)
    {
        packet = default;
        if (data.Length != 16 || data[0] != 1 || (data[1] & 0xE0) != 0)
            return false;

        var parsed = new PenPacket(data[1],
            BinaryPrimitives.ReadUInt32LittleEndian(data[2..6]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[6..10]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[10..12]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..14]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..16]));
        if (parsed.Session == 0
            || (!parsed.InRange && (parsed.Contact || parsed.BarrelDown || parsed.Pressure != 0))
            || (!parsed.Contact && parsed.Pressure != 0)
            || (parsed.BarrelDown && !parsed.BarrelKnown))
            return false;

        packet = parsed;
        return true;
    }

    public string FormatDebug() =>
        $"{(Eraser ? "橡皮" : "笔尖")} | {(Contact ? "接触" : "悬空")} " +
        $"| X={X / 65535.0:F4} Y={Y / 65535.0:F4} | 压感={Pressure / 65535.0:F3} " +
        $"| 侧键={(BarrelKnown ? (BarrelDown ? "按下" : "松开") : "未知")}";
}
