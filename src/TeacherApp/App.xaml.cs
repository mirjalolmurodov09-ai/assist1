using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClassroomControl.Infrastructure.Security;
using ClassroomControl.Infrastructure.Logging;
using ClassroomControl.Platform;
using ClassroomControl.TeacherApp.Views;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.TeacherApp
{
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\ClassroomControl.Teacher";
        private Mutex? _instanceMutex;
        private TeacherRuntime? _runtime;
        private TeacherScreenShareService? _share;
        private PreviewService? _preview;
        private MainViewModel? _main;
        private ILoggerFactory? _loggers;
        private ILogger? _logger;
        private string _logDirectory = string.Empty;
        private DateTime _lastErrorDialog = DateTime.MinValue;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var options = StartupOptions.Parse(e.Args);

            _instanceMutex = new Mutex(true, options.DataDirectory is null ? InstanceMutexName : InstanceMutexName + "." + Math.Abs(options.DataDirectory.GetHashCode()), out var first);
            if (!first)
            {
                MessageBox.Show(Loc.Instance["App.AlreadyRunning"], Loc.Instance["App.Name"], MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            try
            {
                var paths = options.DataDirectory is { } dir ? new AppPaths(dir) : AppPaths.ForCurrentUser("Teacher");
                _logDirectory = paths.LogDirectory;
                RegisterGlobalExceptionHandlers();
                _loggers = LoggerFactory.Create(b => b.AddProvider(new FileLoggerProvider(paths.LogDirectory)));
                _logger = _loggers.CreateLogger("Teacher");

                ISecretProtector protector = OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new AesFileSecretProtector(paths.KeyFile);
                _runtime = TeacherRuntime.Create(paths.DataDirectory, protector, _loggers.CreateLogger<Server>(), o =>
                {
                    if (options.AllowLoopback)
                    {
                        o.AllowLoopbackClients = true;
                        o.BindAddress = System.Net.IPAddress.Loopback;
                    }
                    if (options.TcpPort > 0) o.TcpPort = options.TcpPort;
                    if (options.DiscoveryPort > 0) o.DiscoveryPort = options.DiscoveryPort;
                });
                var theme = new ThemeService();
                theme.Apply(_runtime.Server.Settings.Theme);
                if (options.Language is { } language) Loc.Instance.Language = language;
                if (options.ClassroomCode is { } code) _runtime.Server.SetClassroomCode(code);

                if (!SignIn(options)) { Shutdown(); return; }

                _runtime.Start();
                var ui = new WpfDispatcher();
                _share = new TeacherScreenShareService(_runtime.Server, new GdiScreenSource(), new JpegFrameEncoder());
                var dialogs = new DialogService(_runtime.Server, _runtime.Users, _runtime.Session, _runtime.Monitoring, ui, theme);
                _main = new MainViewModel(_runtime.Server, _runtime.Monitoring, _share, dialogs, ui, _runtime.Session);
                _preview = new PreviewService(_runtime.Monitoring, _runtime.Server, id => _main.Find(id));

                var window = new MainWindow(_main);
                MainWindow = window;
                window.Show();
                _logger.LogInformation("Teacher started.");
                await _runtime.Monitoring.StartAllAsync();

                if (options.UiTestDirectory is { } outDir)
                    _ = new UiTestRunner(_runtime, _main, window, outDir, options.ExpectedComputers, theme).RunAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogCritical(ex, "Teacher failed to start.");
                MessageBox.Show(Loc.Instance["Error.Unexpected"] + Environment.NewLine + ex.Message, Loc.Instance["App.Name"], MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private bool SignIn(StartupOptions options)
        {
            var users = _runtime!.Users;
            if (options.UiTestDirectory is not null)
            {
                // CI only: an account is created and used without asking, so the application can be driven unattended.
                var user = users.HasUsers ? users.Authenticate(UiTestRunner.UserName, UiTestRunner.Password) : users.Create(UiTestRunner.UserName, UiTestRunner.Password, ClassroomServer.Data.Roles.Admin);
                if (user is null) return false;
                _runtime.Session.SignIn(user);
                return true;
            }

            if (!users.HasUsers)
            {
                var setup = new SetupAdminWindow(new SetupAdminViewModel(users));
                if (setup.ShowDialog() != true || setup.User is null) return false;
                _runtime.Session.SignIn(setup.User);
                return true;
            }

            var login = new LoginWindow(new LoginViewModel(users));
            if (login.ShowDialog() != true || login.User is null) return false;
            _runtime.Session.SignIn(login.User);
            return true;
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                _logger?.LogError(args.Exception, "Unexpected error on the UI thread.");
                WriteCrash("UI", args.Exception);
                args.Handled = true;
                if (DateTime.UtcNow - _lastErrorDialog > TimeSpan.FromSeconds(30))
                {
                    _lastErrorDialog = DateTime.UtcNow;
                    MessageBox.Show(Loc.Instance["Error.Unexpected"], Loc.Instance["App.Name"], MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating}).", args.IsTerminating);
                WriteCrash("AppDomain", args.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                _logger?.LogError(args.Exception, "Unexpected error in a background task.");
                args.SetObserved();
            };
        }

        private void WriteCrash(string source, Exception? exception)
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(Path.Combine(_logDirectory, "crash.log"),
                    DateTime.UtcNow.ToString("O") + " [CRT] " + source + ": " + SensitiveDataRedactor.Redact(exception?.ToString()) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Crash log failed: " + ex.Message);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                _preview?.Dispose();
                _main?.Dispose();
                if (_share is not null) await _share.DisposeAsync();
                if (_runtime is not null) await _runtime.DisposeAsync();
                _loggers?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Shutdown error: " + ex.Message);
            }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }
    }

    internal sealed class StartupOptions
    {
        public string? DataDirectory { get; private set; }
        public string? UiTestDirectory { get; private set; }
        public string? ClassroomCode { get; private set; }
        public string? Language { get; private set; }
        public bool AllowLoopback { get; private set; }
        public int ExpectedComputers { get; private set; } = 1;
        public int TcpPort { get; private set; }
        public int DiscoveryPort { get; private set; }

        public static StartupOptions Parse(string[] args)
        {
            var o = new StartupOptions();
            string? Next(int i) => i + 1 < args.Length ? args[i + 1] : null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--data-dir": o.DataDirectory = Next(i); break;
                    case "--ui-test": o.UiTestDirectory = Next(i); break;
                    case "--classroom-code": o.ClassroomCode = Next(i); break;
                    case "--language": o.Language = Next(i); break;
                    case "--allow-loopback": o.AllowLoopback = true; break;
                    case "--expect": o.ExpectedComputers = int.TryParse(Next(i), out var n) ? n : 1; break;
                    case "--tcp-port": o.TcpPort = int.TryParse(Next(i), out var t) ? t : 0; break;
                    case "--discovery-port": o.DiscoveryPort = int.TryParse(Next(i), out var d) ? d : 0; break;
                }
            }
            return o;
        }
    }
}
