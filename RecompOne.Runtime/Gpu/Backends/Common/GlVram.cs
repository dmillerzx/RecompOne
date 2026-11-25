using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

public static class GlVram
{
    public static int Scale { get; set; } = 4;
#if ANDROID
    public static bool TrueColor { get => true; set { } }
    public static InternalFormat Storage => InternalFormat.Rgba8;
#else
    public static bool TrueColor { get; set; }
    public static InternalFormat Storage => TrueColor ? InternalFormat.Rgba8 : InternalFormat.Rgb5A1;
#endif
    public static int Width => VramShadow.Width * Scale;
    public static int Height => VramShadow.Height * Scale;
}
