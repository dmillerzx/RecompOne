using System;

namespace RecompOne.Runtime.Hle;

public static class GlesColorHelper
{
    public static ushort Convert1555To5551(ushort px)
    {
        return (ushort)(
            ((px & 0x001F) << 11) | // Red: 0..4 -> 11..15
            ((px & 0x03E0) << 1)  | // Green: 5..9 -> 6..10
            ((px & 0x7C00) >> 9)  | // Blue: 10..14 -> 1..5
            ((px & 0x8000) != 0 ? 1 : 0) // Alpha: 15 -> 0
        );
    }

    public static ushort Convert5551To1555(ushort px)
    {
        return (ushort)(
            ((px & 0xF800) >> 11) | // Red: 11..15 -> 0..4
            ((px & 0x07C0) >> 1)  | // Green: 6..10 -> 5..9
            ((px & 0x003E) << 9)  | // Blue: 1..5 -> 10..14
            ((px & 0x0001) != 0 ? 0x8000 : 0) // Alpha: 0 -> 15
        );
    }

    public static void Convert1555To5551(ReadOnlySpan<ushort> src, Span<ushort> dst)
    {
        int count = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < count; i++)
        {
            ushort px = src[i];
            dst[i] = (ushort)(
                ((px & 0x001F) << 11) |
                ((px & 0x03E0) << 1)  |
                ((px & 0x7C00) >> 9)  |
                ((px & 0x8000) != 0 ? 1 : 0)
            );
        }
    }

    public static void Convert5551To1555(ReadOnlySpan<ushort> src, Span<ushort> dst)
    {
        int count = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < count; i++)
        {
            ushort px = src[i];
            dst[i] = (ushort)(
                ((px & 0xF800) >> 11) |
                ((px & 0x07C0) >> 1)  |
                ((px & 0x003E) << 9)  |
                ((px & 0x0001) != 0 ? 0x8000 : 0)
            );
        }
    }

    public static void Convert1555ToRgba8(ReadOnlySpan<ushort> src, Span<uint> dst)
    {
        int count = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < count; i++)
        {
            ushort px = src[i];
            uint r = (uint)(((px & 0x001F) * 255 + 15) / 31);
            uint g = (uint)((((px >> 5) & 0x001F) * 255 + 15) / 31);
            uint b = (uint)((((px >> 10) & 0x001F) * 255 + 15) / 31);
            uint a = (uint)((px & 0x8000) != 0 ? 255 : 0);
            dst[i] = r | (g << 8) | (b << 16) | (a << 24);
        }
    }

    public static void ConvertRgba8To1555(ReadOnlySpan<byte> src, Span<ushort> dst)
    {
        int count = Math.Min(src.Length / 4, dst.Length);
        for (int i = 0; i < count; i++)
        {
            int idx = i * 4;
            byte r = src[idx];
            byte g = src[idx + 1];
            byte b = src[idx + 2];
            byte a = src[idx + 3];
            dst[i] = (ushort)(
                ((r >> 3) & 0x1F) |
                (((g >> 3) & 0x1F) << 5) |
                (((b >> 3) & 0x1F) << 10) |
                (a >= 128 ? 0x8000 : 0)
            );
        }
    }
}
