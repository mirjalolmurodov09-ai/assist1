using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.DependencyInjection;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.TestKit;

public sealed class HeadlessAgentOptions
{
    public string DataDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "cc-agent-" + Guid.NewGuid().ToString("N"));
    public string ClassroomCode { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string TeacherAddress { get; set; } = string.Empty;
    public int TeacherPort { get; set; }
    public int DiscoveryPort { get; set; } = 39500;
    /// <summary>Reported as the agent's local IP (simulation label; no real network binding).</summary>
    public string? SimulatedIp { get; set; }
    public bool WriteLogFile { get; set; }
    public Action<AgentOptions>? Configure { get; set; }
    /// <summary>Replace/add services after the defaults (e.g. a fake network monitor).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>The real agent services without any UI. Used by integration tests and the multi-agent simulator.</summary>
public sealed class HeadlessAgent : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IReadOnlyList<IHostedService> _hosted;

    public HeadlessAgent(HeadlessAgentOptions options)
    {
        Options = options;
        var paths = new AppPaths(options.DataDirectory);
        Directory.CreateDirectory(paths.DataDirectory);

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            if (options.WriteLogFile) b.AddStudentAgentFileLogger(paths);
        });
        services.AddStudentAgentCore(paths, new AesFileSecretProtector(paths.KeyFile));
        options.ConfigureServices?.Invoke(services);
        services.Configure<AgentOptions>(o =>
        {
            o.AllowLoopbackTeacher = true;
            o.DiscoveryTargets = ["127.0.0.1"];
            o.ReconnectDelaysSeconds = [1];
            o.HeartbeatIntervalSeconds = 1;
            o.HeartbeatTimeoutSeconds = 5;
            o.DiscoveryTimeoutSeconds = 3;
            o.DiscoveryRetryIntervalMilliseconds = 200;
            o.SupervisorRestartDelaySeconds = 1;
            options.Configure?.Invoke(o);
        });
        if (options.SimulatedIp is not null)
            services.Decorate(sp => new SimulatedDeviceInfoService(sp.GetRequiredService<DeviceInfoService>(), options.SimulatedIp, options.ComputerName));

        _provider = services.BuildServiceProvider();
        var settings = _provider.GetRequiredService<ISettingsService>();
        settings.Load();
        var s = settings.Current;
        s.ClassroomCode = options.ClassroomCode;
        s.StudentName = options.StudentName;
        s.ComputerName = options.ComputerName;
        s.TeacherAddress = options.TeacherAddress;
        s.TeacherPort = options.TeacherPort;
        s.DiscoveryPort = options.DiscoveryPort;
        settings.SaveAsync(s).GetAwaiter().GetResult();
        _hosted = _provider.GetServices<IHostedService>().ToList();
    }

    public HeadlessAgentOptions Options { get; }
    public IServiceProvider Services => _provider;
    public IAgentStatusStore Status => _provider.GetRequiredService<IAgentStatusStore>();
    public ISettingsService Settings => _provider.GetRequiredService<ISettingsService>();
    public IAgentService Agent => _provider.GetRequiredService<IAgentService>();
    public string DeviceId => Settings.Current.DeviceId;

    public async Task StartAsync()
    {
        foreach (var h in _hosted) await h.StartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        foreach (var h in _hosted) await h.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<bool> WaitForStateAsync(ConnectionState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Status.Current.State == state) return true;
            await Task.Delay(25).ConfigureAwait(false);
        }
        return Status.Current.State == state;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
        try { Directory.Delete(Options.DataDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp data; best effort */ }
    }
}

internal static class DecorateExtensions
{
    /// <summary>Replaces IDeviceInfoService with a simulated wrapper around the real implementation.</summary>
    public static void Decorate(this IServiceCollection services, Func<IServiceProvider, IDeviceInfoService> factory)
    {
        var original = services.Single(d => d.ServiceType == typeof(IDeviceInfoService));
        services.Remove(original);
        services.AddSingleton<DeviceInfoService>();
        services.AddSingleton(factory);
    }
}

/// <summary>Reports a configured IP / computer name (e.g. 192.168.1.103 / PC-03) instead of the real ones, for lab simulations.</summary>
public sealed class SimulatedDeviceInfoService : IDeviceInfoService
{
    private readonly IDeviceInfoService _inner;
    private readonly string _ip;
    private readonly string _name;

    public SimulatedDeviceInfoService(IDeviceInfoService inner, string ip, string computerName)
    {
        _inner = inner;
        _ip = ip;
        _name = string.IsNullOrWhiteSpace(computerName) ? inner.ComputerName : computerName;
    }

    public string DeviceId => _inner.DeviceId;
    public string ComputerName => _name;
    public string LocalIp => _ip;
    public string AgentVersion => _inner.AgentVersion;
    public DeviceInfo GetDeviceInfo() => _inner.GetDeviceInfo() with { ComputerName = _name, LocalIp = _ip };
}
