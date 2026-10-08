using ClassroomControl.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassroomControl.TeacherApp.Services;

/// <summary>Creates and owns the database, user service, Local Classroom Server and monitoring for one Teacher application.</summary>
public sealed class TeacherRuntime : IAsyncDisposable
{
    private TeacherRuntime(ClassroomStore store, UserService users, Server server, MonitoringService monitoring, TeacherSession session)
    {
        Store = store;
        Users = users;
        Server = server;
        Monitoring = monitoring;
        Session = session;
    }

    public ClassroomStore Store { get; }
    public UserService Users { get; }
    public Server Server { get; }
    public MonitoringService Monitoring { get; }
    public TeacherSession Session { get; }

    public static TeacherRuntime Create(string dataDirectory, ISecretProtector protector, ILogger<Server>? logger = null, Action<ClassroomServerOptions>? configure = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var options = new ClassroomServerOptions { DataDirectory = dataDirectory };
        var store = new ClassroomStore(options.DatabasePath);
        var settings = new ServerSettings(store);
        options.TcpPort = settings.TcpPort;
        options.DiscoveryPort = settings.DiscoveryPort;
        configure?.Invoke(options);

        var server = new Server(options, store, protector, logger ?? NullLogger<Server>.Instance);
        Loc.Instance.Language = Loc.Languages.Contains(settings.Language) ? settings.Language : Loc.Uzbek;
        return new TeacherRuntime(store, new UserService(store, TimeProvider.System), server, new MonitoringService(server), new TeacherSession(server));
    }

    public void Start() => Server.Start();

    public async ValueTask DisposeAsync()
    {
        Monitoring.Dispose();
        await Server.DisposeAsync().ConfigureAwait(false);
    }
}
