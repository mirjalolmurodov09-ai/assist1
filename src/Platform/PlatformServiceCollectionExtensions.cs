using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomControl.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>Registers the Windows implementations of screen capture, JPEG encoding, input injection, power / process control and metrics.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IScreenSource, GdiScreenSource>();
        services.AddSingleton<IFrameEncoder, JpegFrameEncoder>();
        services.AddSingleton<IInputInjector, SendInputInjector>();
        services.AddSingleton<ISystemControl, WindowsSystemControl>();
        services.AddSingleton<ISystemMetrics, SystemMetrics>();
        services.AddSingleton<KeyboardBlocker>();
        return services;
    }
}
