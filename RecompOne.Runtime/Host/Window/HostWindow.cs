using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

public static class HostWindow
{
    private static IView? _window;
    private static GL? _gl;
    private static ImGuiController? _imgui;
    private static bool _headless;
    private static Gpu? _gpu;

    private static uint _displayTex;
    private static uint _vramTex;
    private static uint _ramTex;
    private static Hle.GlCore? _glBackend;

    private static byte[] _rgbDisplay = [];
    private static byte[] _rgbVram = [];
    private static byte[] _ramFront = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    private static byte[] _ramBack = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    private static Task? _ramTask;
    private static volatile bool _ramReady;
    private static int _ramFrame;

    private static bool _layoutPending = true;
    private static bool _closed;

    private const int RedockCooldownFrames = 8;
    private static int _redockCooldown;

    public static void RequestLayout()
    {
        _layoutPending = true;
    }

    public enum AspectRatioMode
    {
        AutoDevice = 0,      // Dynamic aspect fit for any device (Portrait & Landscape)
        Original_4_3 = 1,    // 4:3 PSX ratio
        Widescreen_16_9 = 2, // 16:9 widescreen
        Stretch = 3          // Stretch fill
    }

    public static AspectRatioMode CurrentAspectRatio = AspectRatioMode.AutoDevice;
    public static volatile bool PendingGpuBackendReset = true;

    public static void RequestGpuReset()
    {
        PendingGpuBackendReset = true;
    }

    static uint _directVao, _directVbo, _directProg;
    static int _uDirectTex;
    static float _directUMax = 1.0f;

    // The aspect the game actually rendered this frame, as reported by the backend. Widescreen
    // makes the frame genuinely wider, so presenting it against a fixed ratio would squash it.
    static float _directAspect = 4f / 3f;

    private static float _dpiScale = 1f;

    public static float DpiScale => _dpiScale;

    private static unsafe float QueryDpiScale()
    {
#if !ANDROID
        try
        {
            var glfw = Silk.NET.GLFW.Glfw.GetApi();
            var monitor = glfw.GetPrimaryMonitor();
            if (monitor != null)
            {
                glfw.GetMonitorContentScale(monitor, out var xs, out var ys);
                var s = MathF.Max(xs, ys);
                if (s >= 0.5f && s <= 8f) return s;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Host] cant read scale: {e.Message}");
        }
#endif

        try
        {
            var fb = _window!.FramebufferSize;
            var size = _window.Size;
            if (size.X > 0 && fb.X > 0)
            {
                var s = (float)fb.X / size.X;
                if (s >= 0.5f && s <= 8f) return s;
            }
        }
        catch
        {
        }

        return 1f;
    }

    private static GraphicsAPI[] ApiChain()
    {
#if ANDROID
        // Android only ever offers GLES, and the desktop fallback chain (4.5 -> 3.3 -> 2.1
        // compatibility) has nothing to fall back to here, so there is a single entry.
        return [new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 0))];
#else
        if (OperatingSystem.IsMacOS())
            return
            [
                new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible,
                    new APIVersion(4, 1))
            ];

        var requested = Hle.GpuBackendFactory.Parse(DisplayOptions.Backend);
        var core45 = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
            new APIVersion(4, 5));
        var core33 = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
            new APIVersion(3, 3));
        var compat21 = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Compatability, ContextFlags.Default,
            new APIVersion(2, 1));

        return requested switch
        {
            Hle.GlBackendKind.Gl21 => [compat21],
            Hle.GlBackendKind.Gl33 => [core33, compat21],
            _ => [core45, core33, compat21]
        };
#endif
    }

    public static void Initialize(string title)
    {
        Settings.EnsureLoaded();
        InterfaceLayout.Load();
        Interp.Interp.Load();

        foreach (var api in ApiChain())
            try
            {
#if ANDROID
                var options = ViewOptions.Default with
                {
                    API = api,
                    VSync = DisplayOptions.VSync,
                };
                _window = Silk.NET.Windowing.Window.GetView(options);
#else
                var options = WindowOptions.Default with
                {
                    Size = new Vector2D<int>(1280, 720),
                    Title = title,
                    VSync = DisplayOptions.VSync,
                    UpdatesPerSecond = 0,
                    FramesPerSecond = 0,
                    WindowState = DisplayOptions.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
                    API = api
                };
                _window = Silk.NET.Windowing.Window.Create(options);
#endif
                FrameClock.VSync = DisplayOptions.VSync;
                Interp.Interp.VSync = DisplayOptions.VSync;
                Interp.Interp.RefreshRate = QueryRefreshRate();
                Console.WriteLine($"[Host] monitor refresh: {Interp.Interp.RefreshRate} hz");
                _window.Load += OnLoad;
                _window.Render += OnRender;
                _window.Closing += OnClosing;
                _window.Initialize();
                Console.WriteLine($"[Host] gl context {api.Version.MajorVersion}.{api.Version.MinorVersion} {api.Profile}");
                return;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(
                    $"[Host] context {api.Version.MajorVersion}.{api.Version.MinorVersion} unavailable: {e.Message}");
                _window = null;
            }

        Console.Error.WriteLine("[Host] no usable gl context were found");
        _headless = true;
    }

    public static string Title
    {
        get => (_window as IWindow)?.Title ?? "";
        set
        {
            if (_window is IWindow w) w.Title = value ?? "";
        }
    }

    public static void SetTitle(string title)
    {
        Title = title;
    }

    private static Silk.NET.Core.RawImage? _pendingIcon;

    public static void SetIcon(byte[] data)
    {
        try
        {
            var rgba = Decode(data, out var w, out var h);
            if (rgba == null)
            {
                Console.Error.WriteLine("[Host] icon format not supported");
                return;
            }

            SetIcon(rgba, w, h);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] failed to set icon: {e.Message}");
        }
    }

    public static void SetIcon(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            Console.Error.WriteLine("[Host] icon pixel buffer does not match its size");
            return;
        }

        var image = new Silk.NET.Core.RawImage(width, height, rgba);
        _pendingIcon = image;
        Apply(image);
    }

    public static void ClearIcon()
    {
        _pendingIcon = null;
        if (_window is not IWindow w) return;
        try
        {
            w.SetWindowIcon(ReadOnlySpan<Silk.NET.Core.RawImage>.Empty);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] failed to clear icon: {e.Message}");
        }
    }

    private static void Apply(Silk.NET.Core.RawImage image)
    {
        if (_window is not IWindow w) return;
        try
        {
            var icons = new[] { image };
            w.SetWindowIcon(icons);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] failed to set icon: {e.Message}");
        }
    }

    private static byte[]? Decode(byte[] data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4) return null;

        if (data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
        {
            var best = LargestIcoEntry(data);
            if (best == null) return null;
            data = best;
        }

        var img = StbImageSharp.ImageResult.FromMemory(data, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        if (img == null) return null;
        width = img.Width;
        height = img.Height;
        return img.Data;
    }

    private static byte[]? LargestIcoEntry(byte[] ico)
    {
        int count = BitConverter.ToUInt16(ico, 4);
        var bestArea = -1;
        byte[]? best = null;

        for (var i = 0; i < count; i++)
        {
            var e = 6 + i * 16;
            if (e + 16 > ico.Length) break;

            var w = ico[e] == 0 ? 256 : ico[e];
            var h = ico[e + 1] == 0 ? 256 : ico[e + 1];
            var size = BitConverter.ToInt32(ico, e + 8);
            var offset = BitConverter.ToInt32(ico, e + 12);
            if (size <= 0 || offset < 0 || offset + size > ico.Length) continue;

            var png = size > 8 && ico[offset] == 0x89 && ico[offset + 1] == 0x50 &&
                      ico[offset + 2] == 0x4E && ico[offset + 3] == 0x47;
            if (!png) continue;

            var area = w * h;
            if (area <= bestArea) continue;
            bestArea = area;
            best = ico.AsSpan(offset, size).ToArray();
        }

        return best;
    }

    public static void Present(Gpu? gpu)
    {
        _gpu = gpu;
        if (_headless || _window == null) return;
        try
        {
            _window.DoEvents();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }

        if (_window.IsClosing)
        {
            Runtime.Shutdown();
            Environment.Exit(0);
        }

        InputManager.Poll();
        HandleShortcuts();

        _window.DoRender();
        FrameClock.MarkPresent();
    }

    public static bool Ready => !_headless && _window != null;
    
    public static void PumpEvents()
    {
        if (_headless || _window == null) return;
        
        try
        {
            _window.DoEvents();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
        
        if (_window.IsClosing)
        {
            Runtime.Shutdown();
            Environment.Exit(0);
        }
        
        InputManager.Poll();
        
        HandleShortcuts();
    }

    private static void HandleShortcuts()
    {
        if (InputManager.ConsumeTopBarToggle())
            InterfaceOptions.HideMenuBar.Value = !InterfaceOptions.HideMenuBar;

        if (InputManager.ConsumeFullscreenToggle())
        {
            DisplayOptions.Fullscreen.Value = !DisplayOptions.Fullscreen;
            SetFullscreen(DisplayOptions.Fullscreen);
        }
    }
    
    public static void Compose(Gpu? gpu, bool counts = true)
    {
        _gpu = gpu;
        if (_headless || _window == null) return;
        
        try
        {
            _window.DoRender();
        }
        catch (NotImplementedException) //doesnt fucking work
        {
        }
        
        if (counts) FrameClock.MarkPresent();
    }
    
    internal static void Pump()
    {
        if (_headless || _window == null) return;
        try
        {
            _window.DoEvents();
        }
        catch
        {
        }

        if (_window.IsClosing)
        {
            Runtime.Shutdown();
            Environment.Exit(0);
        }

        _window.DoRender();
    }

    public static void Shutdown()
    {
        if (!_headless && _window != null && !_window.IsClosing)
            _window.Close();
        InputManager.Shutdown();
    }

    public static void SetFullscreen(bool on)
    {
        if (_window is not IWindow w) return;
        w.WindowState = on ? WindowState.Fullscreen : WindowState.Normal;
        if (on) SetAutoIconify(false);
    }

    private static unsafe void SetAutoIconify(bool on)
    {
#if !ANDROID
        try
        {
            var handle = (_window as IWindow)?.Native?.Glfw;
            if (handle is not { } h) return;
            Silk.NET.GLFW.Glfw.GetApi().SetWindowAttrib(
                (Silk.NET.GLFW.WindowHandle*)h,
                Silk.NET.GLFW.WindowAttributeSetter.AutoIconify, on);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Host] auto-iconify unavailable: {e.Message}");
        }
#endif
    }

    public static bool IsKeyDown(Key k)
    {
        return InputManager.IsKeyDown(k);
    }

    /// <summary>Number of attached pads the game can actually drive.</summary>
    public static int ConnectedPadCount => InputManager.Devices.Count;

    /// <summary>True when the given player slot has a pad open.</summary>
    public static bool IsPadConnected(int pad) => InputManager.IsPadConnected(pad);

    /// <summary>
    /// Every joystick the system reports, and whether SDL has a button mapping for it. A host
    /// with no console to read - the Android build - needs this to tell "no pad attached"
    /// apart from "pad attached that we cannot drive".
    /// </summary>
    public static IReadOnlyList<(int Index, string Name, bool IsGameController)> DescribeJoysticks()
        => InputManager.DescribeJoysticks();

    /// <summary>Re-opens controllers now instead of waiting for the next device change.</summary>
    public static void RescanControllers() => InputManager.RefreshDevices();

    public static Action? OnMenuRequested
    {
        get => InputManager.OnMenuRequested;
        set => InputManager.OnMenuRequested = value;
    }

    public static void RequestDiscPath()
    {
        PopupManager.Open<DiscPickerPopup>();
    }

    public static void WaitForValidDisc() // wait for disc path to be valid before running it!!
    {
        if (_headless || _window == null) return;

        while (StartupNoticePopup.NeedsAck)
            HandOverFrame();

        while (true)
        {
            var path = Settings.System.DiscPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && Runtime.ValidateDisc(path) == null)
                return;

            HandOverFrame();
        }
    }
    
    private static void HandOverFrame()
    {
        var closing = false;

        GpuJobs.Run(() =>
        {
            try
            {
                _window!.DoEvents();
            }
            catch
            {
            }

            if (_window!.IsClosing)
            {
                closing = true;
                return;
            }

            InputManager.Poll();
            _window.DoRender();
        });

        if (!closing) return;

        Runtime.Shutdown();
        Environment.Exit(0);
    }

    private static void OnLoad()
    {
        var input = _window!.CreateInput();
        InputManager.Initialize(input);

        if (_pendingIcon is { } icon) Apply(icon);

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);

        var fb = _window!.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _window.FramebufferResize += size => _gl?.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _displayTex = CreateTexture(_gl);
        _vramTex = CreateTexture(_gl);
        _ramTex = CreateTexture(_gl);

        Hle.GlVram.Scale = DisplayOptions.RenderScale;
        Hle.GlVram.TrueColor = DisplayOptions.TrueColor;
        _glBackend = (Hle.GlCore)Hle.GpuBackendFactory.Create(_gl,
            Hle.GpuBackendFactory.Parse(DisplayOptions.Backend));
        _glBackend.InitGl();
        Hle.GpuHle.Active = _glBackend.Ready;
#if ANDROID
        // The Android host drives Entry.Run directly rather than Runtime.Run's interpolated
        // present loop, so the backend presents from the same thread that runs the game.
        Hle.GpuHle.Backend = _glBackend;
#else
        Hle.GpuHle.Backend = new Interp.InterpBackend(_glBackend);
#endif
        ApplySwapInterval();

        try
        {
            _imgui = new ImGuiController(_gl, _window, input, ConfigureImGui);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] ImGui initialization skipped: {e.Message}");
            _imgui = null;
        }
        Interp.InterpOptions.TargetFps.Changed += _ => ApplySwapInterval();

        PanelManager.Register(new OutputPanel());
        PanelManager.Register(new VramViewerPanel());
        PanelManager.Register(new TextureInspectorPanel());
        PanelManager.Register(new CpuStatePanel());
        PanelManager.Register(new RamMapPanel());
        PanelManager.Register(new MemoryEditorPanel());
        PanelManager.Register(new SpuViewerPanel());
        PanelManager.Register(new CdDebugPanel());
        PanelManager.Register(new InterpDebugPanel());
        PanelManager.Register(new ConsolePanel());
        PanelManager.Register(new OverlayEventsPanel());
        PanelManager.Register(new AssetPackEditor());

        PopupManager.Register(new SettingsPopup());
        PopupManager.Register(new ModsPopup());
        PopupManager.Register(new ModHubPopup());
        PopupManager.Register(new AssetPacksPopup());
        PopupManager.Register(new NewPackPopup());
        PopupManager.Register(new ConfirmPopup());
        PopupManager.Register(new ModLoadingPopup());
        PopupManager.Register(new NoticePopup());
        PopupManager.Register(new CrashPopup());
        PopupManager.Register(new StartupNoticePopup());
        PopupManager.Register(new DiscPickerPopup());

        MainMenuBar.RegisterBuiltins();

        InterfacePage.Register();
        LanguagePage.Register();
        InputPage.Register();
        DisplayPage.Register();
        AudioPage.Register();
        PathsPage.Register();

        InterfaceLayout.ApplyPanels(PanelManager.Panels);

        var cdPath = Settings.System.DiscPath;
        if (string.IsNullOrWhiteSpace(cdPath) || !File.Exists(cdPath) || Runtime.ValidateDisc(cdPath) != null)
            PopupManager.Open<DiscPickerPopup>();
    }

    private static void ConfigureImGui()
    {
        _dpiScale = QueryDpiScale();
        Console.WriteLine($"[Host] display scale: {_dpiScale:0.##}x");

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        io.FontGlobalScale = InterfaceOptions.Scale;
        unsafe
        {
            io.NativePtr->IniFilename = null;
        }

#if !ANDROID
        // Android builds an ImGui context (OnRender still ticks it) but never draws a single
        // ImGui window - RenderDirectDisplay puts the game on screen and the menu is native
        // Android views. Building the atlas would still rasterise the merged Japanese and
        // Simplified Chinese ranges into a texture nothing samples, on the device least able
        // to spare the memory, so leave ImGui on its small built-in font there.
        FontSet.Load(16f * _dpiScale);
#endif
        Localization.Load();
        Theme.Load();

        if (InterfaceLayout.ApplyImGui())
            _layoutPending = false;

        if (DisplayOptions.Fullscreen) SetAutoIconify(false);
    }

    public static void SetVSync(bool on)
    {
        if (_window != null) _window.VSync = on;
        FrameClock.VSync = on;
        Interp.Interp.VSync = on;
        ApplySwapInterval();
        FrameClock.Resync();
    }
    
    public static void AdvanceFrame()
    {
        _glBackend?.AdvanceFrame();
    }
    
    public static void RefreshVSync()
    {
        ApplySwapInterval();
    }
    
    private static void ApplySwapInterval()
    {
        if (_headless || _window == null) return;
        
        var refresh = QueryRefreshRate();
        if (refresh > 0 && refresh != Interp.Interp.RefreshRate)
        {
            Interp.Interp.RefreshRate = refresh;
            Console.WriteLine($"[Host] monitor refresh: {refresh} hz");
        }
        
        var target = Features.FrameInterpolation ? Interp.InterpOptions.TargetFps.Value : 0;
        var divisor = target > 0 && refresh > target && refresh % target == 0 ? refresh / target : 1;
        var interval = Interp.Interp.VSync ? -divisor : 0;
        if (!SetSwapInterval(interval)) interval = SetSwapInterval(divisor) ? divisor : 0;
        
        Console.WriteLine($"[Host] swap interval: {interval}" +
                          (interval < 0 ? " (adaptive)" : ""));
    }
    
    private static bool SetSwapInterval(int interval)
    {
        try
        {
            Silk.NET.GLFW.Glfw.GetApi().SwapInterval(interval);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private static unsafe int QueryRefreshRate()
    {
        try
        {
            var glfw = Silk.NET.GLFW.Glfw.GetApi();
            var monitor = glfw.GetPrimaryMonitor();
            if (monitor == null) return 0;
            
            var mode = glfw.GetVideoMode(monitor);
            if (mode == null) return 0;
            
            var rate = mode->RefreshRate;
            return rate is > 0 and <= 1000 ? rate : 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Host] cant read refresh rate: {e.Message}");
            return 0;
        }
    }

    private static int _diagDirectCount;
    static unsafe void RenderDirectDisplay(GL gl, int screenW, int screenH, uint overrideTex = 0, float uMax = 1.0f, float contentAspect = 0f)
    {
        uint tex = overrideTex != 0 ? overrideTex : OutputPanel.TextureId;
        if (tex == 0) tex = _displayTex;
        if (tex == 0) tex = _vramTex;
        if (tex == 0 && _glBackend is { Ready: true }) tex = _glBackend.PresentDisplay(0, 0, 320, 240, false).tex;
        if (tex == 0) return;

        float um = uMax > 0f ? uMax : _directUMax;

        if (_directProg == 0)
        {
            string vs = """
                #version 300 es
                precision highp float;
                precision highp int;
                layout(location = 0) in vec2 aPos;
                layout(location = 1) in vec2 aUv;
                out vec2 vUv;
                void main() {
                    vUv = aUv;
                    gl_Position = vec4(aPos, 0.0, 1.0);
                }
                """;
            string fs = """
                #version 300 es
                precision highp float;
                precision highp int;
                in vec2 vUv;
                uniform sampler2D uTex;
                out vec4 FragColor;
                void main() {
                    FragColor = texture(uTex, vUv);
                }
                """;
            _directProg = Hle.GlShaders.Build(gl, vs, fs, "direct_present");
            if (_directProg != 0)
            {
                _uDirectTex = gl.GetUniformLocation(_directProg, "uTex");
                _directVao = gl.GenVertexArray();
                gl.BindVertexArray(_directVao);
                _directVbo = gl.GenBuffer();
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, _directVbo);
                float[] quad = {
                    -1f, -1f, 0f, 1f,
                     1f, -1f, 1f, 1f,
                    -1f,  1f, 0f, 0f,
                     1f,  1f, 1f, 0f
                };
                fixed (float* qp = quad)
                    gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), qp, BufferUsageARB.StaticDraw);
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
                gl.BindVertexArray(0);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            }
        }

        if (_directProg == 0 || _directVbo == 0) return;

        // Present at whatever ratio the game rendered at. Choosing the ratio here instead was
        // what stretched the picture: asking for 16:9 only widened the destination rectangle
        // while the frame inside it was still 4:3, so every pixel got pulled sideways. A wider
        // picture has to come from rendering wider (WidescreenPatch), not from scaling.
        float content = contentAspect > 0f ? contentAspect : 4f / 3f;

        int vx, vy, vw, vh;
        if (CurrentAspectRatio == AspectRatioMode.Stretch)
        {
            // The one mode that distorts on purpose.
            vx = vy = 0;
            vw = screenW;
            vh = screenH;
        }
        else if (screenW < screenH)
        {
            // --- PORTRAIT ---
            // Across the width, centred, but capped so the touch pad keeps its room below.
            vw = screenW;
            vh = (int)(vw / content);
            int maxH = (int)(screenH * 0.55f);
            if (vh > maxH)
            {
                vh = maxH;
                vw = (int)(vh * content);
            }
            vx = (screenW - vw) / 2;
            vy = (screenH - vh) / 2;
        }
        else
        {
            // --- LANDSCAPE ---
            // Letterbox or pillarbox, never squash.
            float screenRatio = (float)screenW / screenH;
            if (screenRatio > content)
            {
                vh = screenH;
                vw = (int)(vh * content);
            }
            else
            {
                vw = screenW;
                vh = (int)(vw / content);
            }
            vx = (screenW - vw) / 2;
            vy = (screenH - vh) / 2;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)screenW, (uint)screenH);
        gl.ColorMask(true, true, true, true);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        gl.Viewport(vx, vy, (uint)vw, (uint)vh);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.CullFace);

        gl.UseProgram(_directProg);
        gl.BindVertexArray(_directVao);

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _directVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.Uniform1(_uDirectTex, 0);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        gl.BindVertexArray(0);
    }

    private static void OnRender(double dt)
    {
        var gl = _gl!;
        if (_imgui != null)
        {
            try
            {
                _imgui.Update((float)dt);
                Autohide.Update();
            }
            catch
            {
                _imgui = null;
            }
        }

        if (PendingGpuBackendReset)
        {
            PendingGpuBackendReset = false;
            try
            {
                Hle.GlVram.Scale = DisplayOptions.RenderScale;
                _glBackend?.Dispose();
                _glBackend = (Hle.GlCore)Hle.GpuBackendFactory.Create(gl,
                    Hle.GpuBackendFactory.Parse(DisplayOptions.Backend));
                _glBackend.InitGl();
                Hle.GpuHle.Active = _glBackend.Ready;
#if ANDROID
                Hle.GpuHle.Backend = _glBackend;
#else
                Hle.GpuHle.Backend = new Interp.InterpBackend(_glBackend);
#endif
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Host] GPU backend reset failed: {e.Message}");
            }
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var fbDef = _window!.FramebufferSize;
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        var clear = Theme.Background;
        gl.ClearColor(clear.X, clear.Y, clear.Z, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        Runtime.RamLog.Tick();
        Settings.SaveIfDue();
        ToolUi.Pump(); //update thread state
        Assets.AssetReplacerManager.Instance.Pump();
        Memory.RamLogger.TrackReads =
            PanelManager.Get<RamMapPanel>()?.IsOpen == true ||
            PanelManager.Get<MemoryEditorPanel>()?.IsOpen == true;
        Memory.RamLogger.TrackWrites = Memory.RamLogger.TrackReads;

        var gpu = _gpu;
        uint directTex = 0;
        float directAspect = 4f / 3f;
        if (gpu != null)
        {
#if ANDROID
            var display = new Interp.DisplayState(gpu.DisplayX, gpu.DisplayY,
                gpu.DisplayWidth > 0 ? gpu.DisplayWidth : 320,
                gpu.DisplayHeight > 0 ? gpu.DisplayHeight : 240,
                gpu.Display24Bit, gpu.DisplayEnabled);
#else
            var display = new Interp.DisplayState(gpu.DisplayX, gpu.DisplayY, gpu.DisplayWidth, gpu.DisplayHeight,
                gpu.Display24Bit, gpu.DisplayEnabled);
#endif
            var backend = Interp.Interp.Backend;
            var recorded = default(Interp.DisplayState);
            var shown = backend != null && backend.TryShownDisplay(out recorded) &&
                        (recorded.Width != display.Width || recorded.Height != display.Height ||
                         recorded.Rgb24 != display.Rgb24 || recorded.Enabled != display.Enabled);
            if (shown) display = recorded;

#if ANDROID
            if (Hle.GpuHle.Active && _glBackend is { Ready: true })
#else
            if (Hle.GpuHle.Active && _glBackend is { Ready: true } && display.Enabled)
#endif
            {
                var wf = _window!.FramebufferSize;
                var dispX = display.X;
                var dispY = display.Y;
                if (!shown) backend?.MapDisplay(ref dispX, ref dispY, display.Width, display.Height);
                var (tex, tw, th, aspect) = _glBackend.PresentDisplay(
                    dispX, dispY,
                    display.Width, display.Height,
                    display.Rgb24,
                    OutputPanel.ViewWidth, OutputPanel.ViewHeight);
                if (tex != 0)
                {
                    OutputPanel.SetTexture(tex, tw, th, aspect);
                    directTex = tex;
                    directAspect = aspect > 0f ? aspect : 4f / 3f;
                }
                else
                {
                    UploadDisplayTexture(gl, gpu);
                    directTex = OutputPanel.TextureId;
                    directAspect = Hle.GpuHle.OutputAspect;
                }

                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.Viewport(0, 0, (uint)wf.X, (uint)wf.Y);
            }
            else
            {
                UploadDisplayTexture(gl, gpu);
                directTex = OutputPanel.TextureId;
                directAspect = Hle.GpuHle.OutputAspect;
            }

            if (PanelManager.Get<VramViewerPanel>()?.IsOpen == true)
                UploadVramTexture(gl, gpu);
        }

        if (PanelManager.Get<RamMapPanel>()?.IsOpen == true)
        {
            QueueRamConvert();
            if (_ramReady) FlushRamTexture(gl);
        }

#if ANDROID
        RenderDirectDisplay(gl, fbDef.X, fbDef.Y, directTex, 1f, directAspect);
#else
        var overlay = Autohide.BarOverlay;
        if (!overlay && Autohide.BarVisible)
            MainMenuBar.Draw();

        DrawDockspace(overlay);
        PanelManager.DrawPanels();
        if (overlay && Autohide.BarVisible)
            MainMenuBar.Draw();
        PopupManager.Draw();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        _imgui?.Render();
#endif
    }

    private static void DrawDockspace(bool overlay)
    {
        var viewport = ImGui.GetMainViewport();
        var area = overlay ? viewport.Size : viewport.WorkSize;
        ImGui.SetNextWindowPos(overlay ? viewport.Pos : viewport.WorkPos);
        ImGui.SetNextWindowSize(area);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoDocking |
                                           ImGuiWindowFlags.NoTitleBar |
                                           ImGuiWindowFlags.NoCollapse |
                                           ImGuiWindowFlags.NoResize |
                                           ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoBringToFrontOnFocus |
                                           ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##DockHost", hostFlags);
        ImGui.PopStyleVar(3);
        var dockId = ImGui.GetID("##MainDock");
        var openCount = PanelManager.Panels.Count(p => p.IsOpen && p is not IFloatingPanel);
        var dockFlags = openCount <= 1 ? (ImGuiDockNodeFlags)4096 : ImGuiDockNodeFlags.None;
        ImGui.DockSpace(dockId, Vector2.Zero, dockFlags);

        if (openCount <= 1 && !OutputPanel.IsDocked && _redockCooldown == 0)
            _layoutPending = true;

        if (_redockCooldown > 0) _redockCooldown--;

        if (_layoutPending)
        {
            _layoutPending = false;
            _redockCooldown = RedockCooldownFrames;
            if (PanelManager.Get<OutputPanel>() is { } output)
                DockBuilder.SetupCenterLayout(dockId, area, output.Title());
        }

        ImGui.End();
    }

    private static void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        InterfaceLayout.Save(PanelManager.Panels);
        Settings.SaveIfDirty();
        PanelManager.Shutdown();
        PopupManager.Shutdown();
        _glBackend?.Dispose();
        _imgui?.Dispose();
        _gl?.DeleteTexture(_displayTex);
        _gl?.DeleteTexture(_vramTex);
        _gl?.DeleteTexture(_ramTex);
    }

    public static uint UploadPng(byte[] png)
    {
        try
        {
            var img = StbImageSharp.ImageResult.FromMemory(png, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (img == null || img.Width <= 0 || img.Height <= 0) return 0;

            int w = img.Width, h = img.Height;
            if (w == h) return UploadTexture(img.Data, w, h);

            var s = Math.Min(w, h);
            var ox = (w - s) / 2;
            var oy = (h - s) / 2;
            var square = new byte[s * s * 4];
            for (var y = 0; y < s; y++)
                Array.Copy(img.Data, ((oy + y) * w + ox) * 4, square, y * s * 4, s * 4);
            return UploadTexture(square, s, s);
        }
        catch
        {
            return 0;
        }
    }

    public static uint UploadTexture(byte[] rgba, int width, int height)
    {
        if (_gl == null || width <= 0 || height <= 0) return 0;
        var needed = width * height * 4;
        if (rgba.Length < needed) return 0;
        var tex = CreateTexture(_gl);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan(0, needed));
        return tex;
    }

    private static uint CreateTexture(GL gl)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    private static void UploadDisplayTexture(GL gl, Gpu gpu)
    {
        int w = gpu.DisplayWidth, h = gpu.DisplayHeight;
        if (!gpu.DisplayEnabled || w <= 0 || h <= 0) return;
        var needed = w * h * 3;
        if (_rgbDisplay.Length < needed) _rgbDisplay = new byte[needed];
        ConvertDisplay(gpu, w, h);
        gl.BindTexture(TextureTarget.Texture2D, _displayTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, (uint)w, (uint)h, 0,
            PixelFormat.Rgb, PixelType.UnsignedByte, _rgbDisplay.AsSpan(0, needed));
        OutputPanel.SetTexture(_displayTex, w, h);
    }

    private static ushort[] _vramView = new ushort[Gpu.VramWidth * Gpu.VramHeight];

    private static void UploadVramTexture(GL gl, Gpu gpu)
    {
        const int sz = Gpu.VramWidth * Gpu.VramHeight * 3;
        if (_rgbVram.Length < sz) _rgbVram = new byte[sz];
        ushort[] src;
        if (Hle.GpuHle.Active && _glBackend is { Ready: true })
        {
            _glBackend.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, _vramView);
            src = _vramView;
        }
        else
        {
            src = gpu.Vram;
        }

        ConvertVramToBuffer(src, _rgbVram);
        gl.BindTexture(TextureTarget.Texture2D, _vramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, Gpu.VramWidth, Gpu.VramHeight, 0,
            PixelFormat.Rgb, PixelType.UnsignedByte, _rgbVram.AsSpan(0, sz));
        VramViewerPanel.SetTexture(_vramTex, Gpu.VramWidth, Gpu.VramHeight);
    }

    private static void QueueRamConvert()
    {
        if (_ramTask is { IsCompleted: false }) return;
        if (++_ramFrame < 6) return;
        _ramFrame = 0;
        var psMem = Runtime.Mem as Memory.PSMemory;
        if (psMem == null) return;
        var ram = psMem.RamBuffer;
        var back = _ramBack;
        _ramTask = Task.Run(() => Runtime.RamLog.BuildTexture(ram, back))
            .ContinueWith(_ =>
            {
                (_ramFront, _ramBack) = (_ramBack, _ramFront);
                _ramReady = true;
            }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private static void FlushRamTexture(GL gl)
    {
        _ramReady = false;
        gl.BindTexture(TextureTarget.Texture2D, _ramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            Memory.RamLogger.Width, Memory.RamLogger.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _ramFront);
        RamMapPanel.SetTexture(_ramTex);
    }

    private static void ConvertDisplay(Gpu gpu, int w, int h)
    {
        var vram = gpu.Vram;
        int dx = gpu.DisplayX, dy = gpu.DisplayY;
        var o = 0;
        if (gpu.Display24Bit)
            for (var y = 0; y < h; y++)
            {
                var lineByte = ((dy + y) * Gpu.VramWidth + dx) * 2;
                for (var x = 0; x < w; x++)
                {
                    var bo = lineByte + x * 3;
                    _rgbDisplay[o++] = VramByte(vram, bo);
                    _rgbDisplay[o++] = VramByte(vram, bo + 1);
                    _rgbDisplay[o++] = VramByte(vram, bo + 2);
                }
            }
        else
            for (var y = 0; y < h; y++)
            {
                var line = ((dy + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                for (var x = 0; x < w; x++)
                {
                    var px = vram[line + ((dx + x) & (Gpu.VramWidth - 1))];
                    _rgbDisplay[o++] = (byte)((px & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 5) & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 10) & 0x1F) << 3);
                }
            }
    }

    private static void ConvertVramToBuffer(ushort[] vram, byte[] output)
    {
        var o = 0;
        for (var y = 0; y < Gpu.VramHeight; y++)
        for (var x = 0; x < Gpu.VramWidth; x++)
        {
            var px = vram[y * Gpu.VramWidth + x];
            output[o++] = (byte)((px & 0x1F) << 3);
            output[o++] = (byte)(((px >> 5) & 0x1F) << 3);
            output[o++] = (byte)(((px >> 10) & 0x1F) << 3);
        }
    }

    private static byte VramByte(ushort[] vram, int byteOffset)
    {
        var hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        var v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }
}
