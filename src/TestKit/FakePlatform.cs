using System.Collections.Concurrent;
using ClassroomControl.StudentAgent.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomControl.TestKit;

/// <summary>A screen that can be told what to look like: static, or with a moving square (to exercise dirty regions).</summary>
public sealed class SyntheticScreen : IScreenSource
{
    private int _tick;
    public int NativeWidth { get; set; } = 1280;
    public int NativeHeight { get; set; } = 720;
    public bool Animate { get; set; }
    public bool Available { get; set; } = true;
    public int CaptureCount;

    public RawFrame? Capture(int maxWidth)
    {
        Interlocked.Increment(ref CaptureCount);
        if (!Available) return null;
        var width = maxWidth > 0 && NativeWidth > maxWidth ? maxWidth : NativeWidth;
        var height = Math.Max(1, NativeHeight * width / NativeWidth);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)40);
        if (Animate)
        {
            var step = Interlocked.Increment(ref _tick);
            var x = step * 8 % Math.Max(1, width - 40);
            for (var row = 10; row < Math.Min(height, 42); row++)
                for (var col = x; col < Math.Min(width, x + 32); col++)
                    pixels[row * stride + col * 4 + 2] = 255;
        }
        return new RawFrame(width, height, stride, pixels);
    }
}

public sealed class RecordingEncoder : IFrameEncoder
{
    public string Format => "jpeg";

    public byte[] Encode(RawFrame frame, PixelRegion region, int quality) =>
        System.Text.Encoding.ASCII.GetBytes($"JPEG q{quality} {region.X},{region.Y} {region.Width}x{region.Height} of {frame.Width}x{frame.Height}");
}

public sealed class RecordingInput : IInputInjector
{
    public ConcurrentQueue<MouseEventMessage> Mouse { get; } = new();
    public ConcurrentQueue<KeyboardEventMessage> Keys { get; } = new();
    public void Inject(MouseEventMessage mouse) => Mouse.Enqueue(mouse);
    public void Inject(KeyboardEventMessage key) => Keys.Enqueue(key);
}

public sealed class RecordingSystem : ISystemControl
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public int StopResult { get; set; } = 1;
    public int StopCalls;
    public void Restart(int delaySeconds, string? reason) => Calls.Enqueue($"restart:{delaySeconds}");
    public void Shutdown(int delaySeconds, string? reason) => Calls.Enqueue($"shutdown:{delaySeconds}");

    public StartApplicationResult StartApplication(string target, string? arguments)
    {
        Calls.Enqueue($"start:{target}:{arguments}");
        return new StartApplicationResult(!target.Contains("missing", StringComparison.Ordinal), "Application started");
    }

    public int StopApplication(string processName)
    {
        Interlocked.Increment(ref StopCalls);
        Calls.Enqueue($"stop:{processName}");
        return StopResult;
    }
}

public sealed class RecordingLockScreen : ILockScreen
{
    public bool IsShown { get; private set; }
    public string? Message { get; private set; }
    public void Show(string message) { IsShown = true; Message = message; }
    public void Hide() => IsShown = false;
}

public sealed class RecordingNotifier : IUserNotifier
{
    public ConcurrentQueue<(string Title, string Text)> Shown { get; } = new();
    public void Show(string title, string text) => Shown.Enqueue((title, text));
}

public sealed class RecordingViewer : ITeacherScreenViewer
{
    public bool IsOpen { get; private set; }
    public string? Title { get; private set; }
    public ConcurrentQueue<ScreenFrameMessage> Frames { get; } = new();
    public void Show(string? title) { IsOpen = true; Title = title; }
    public void Update(ScreenFrameMessage frame) => Frames.Enqueue(frame);
    public void Close() => IsOpen = false;
}

public sealed class FakeMetrics : ISystemMetrics
{
    public long BootTime { get; set; } = 1_700_000_000;
    public SystemSnapshot Read() => new(17, 8L * 1024 * 1024 * 1024, 3L * 1024 * 1024 * 1024, BootTime);
}

/// <summary>The fake Windows for a headless agent. Tests read what the agent "did" from these objects.</summary>
public sealed class FakePlatform
{
    public SyntheticScreen Screen { get; } = new();
    public RecordingEncoder Encoder { get; } = new();
    public RecordingInput Input { get; } = new();
    public RecordingSystem System { get; } = new();
    public RecordingLockScreen Lock { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public RecordingViewer Viewer { get; } = new();
    public FakeMetrics Metrics { get; } = new();

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IScreenSource>(Screen);
        services.AddSingleton<IFrameEncoder>(Encoder);
        services.AddSingleton<IInputInjector>(Input);
        services.AddSingleton<ISystemControl>(System);
        services.AddSingleton<ILockScreen>(Lock);
        services.AddSingleton<IUserNotifier>(Notifier);
        services.AddSingleton<ITeacherScreenViewer>(Viewer);
        services.AddSingleton<ISystemMetrics>(Metrics);
    }
}
