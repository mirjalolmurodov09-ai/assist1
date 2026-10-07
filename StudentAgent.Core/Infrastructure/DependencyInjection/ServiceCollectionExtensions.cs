using ClassroomControl.StudentAgent.Commands;
using ClassroomControl.StudentAgent.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Infrastructure.Logging;
using ClassroomControl.StudentAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers every UI-independent Student Agent service. Hosts add their own <see cref="ViewModels.IUiDispatcher"/> and window service.</summary>
    public static IServiceCollection AddStudentAgentCore(this IServiceCollection services, IAppPaths? paths = null, ISecretProtector? protector = null)
    {
        paths ??= AppPaths.ForCurrentUser();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(paths);
        services.TryAddSingleton(protector ?? (OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new AesFileSecretProtector(paths.KeyFile)));

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ILocalNetworkInfo, LocalNetworkInfo>();
        services.AddSingleton<IDeviceInfoService, DeviceInfoService>();
        services.AddSingleton<IClassroomKeyProvider, ClassroomKeyProvider>();
        services.AddSingleton<IAgentStatusStore, AgentStatusStore>();
        services.AddSingleton<IDiscoveryService, DiscoveryService>();
        services.AddSingleton<IConnectionService, ConnectionService>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IHeartbeatService, HeartbeatService>();
        services.AddSingleton<IReconnectService, ReconnectService>();
        services.AddSingleton<INetworkMonitor, NetworkMonitor>();
        services.AddSingleton<ISessionRunner, SessionRunner>();
        services.AddSingleton<IRunKeyStore, WindowsRunKeyStore>();
        services.AddSingleton<IExecutablePathProvider, ProcessExecutablePathProvider>();
        services.AddSingleton<IStartupService, StartupService>();

        services.AddSingleton<ICommandHandler, PingCommandHandler>();
        services.AddSingleton<ICommandHandler, GetStatusCommandHandler>();
        services.AddSingleton<ICommandHandler, GetDeviceInfoCommandHandler>();
        foreach (var name in CommandNames.Reserved)
            services.AddSingleton<ICommandHandler>(new ReservedCommandHandler(name));
        services.AddSingleton<ICommandDispatcher, CommandDispatcher>();

        services.AddSingleton<AgentService>();
        services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<AgentService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AgentService>());
        return services;
    }

    public static ILoggingBuilder AddStudentAgentFileLogger(this ILoggingBuilder logging, IAppPaths paths)
    {
        logging.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(paths.LogDirectory));
        return logging;
    }
}
