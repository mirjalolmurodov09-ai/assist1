using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClassroomControl.Platform;
using ClassroomControl.StudentAgent.Features;
using ClassroomControl.StudentAgent.Infrastructure.DependencyInjection;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Services;
using ClassroomControl.StudentAgent.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent
{
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\ClassroomControl.StudentAgent";
        private const string ShowEventName = @"Local\ClassroomControl.StudentAgent.Show";
        private const int ErrorDialogCooldownSeconds = 30;

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showWait;
        private IHost? _host;
        private TrayIconService? _tray;
        private FeatureIndicatorService? _indicator;
        private ILogger<App>? _logger;
        private AppPaths? _paths;
        private DateTime _lastErrorDialog = DateTime.MinValue;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Contains(StartupService.UnregisterArgument, StringComparer.OrdinalIgnoreCase))
            {
                // Uninstaller helper: no UI, no agent.
                new StartupService(new WindowsRunKeyStore(), new ProcessExecutablePathProvider()).SetEnabled(false);
                Shutdown();
                return;
            }

            // One agent per Windows user session: a second launch just brings the first window forward.
            var suffix = CommandLine.Parse(e.Args).DataDirectory is { } custom ? "." + Math.Abs(custom.GetHashCode()) : string.Empty;
            _instanceMutex = new Mutex(true, InstanceMutexName + suffix, out var isFirstInstance);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + suffix);
            if (!isFirstInstance)
            {
                _showEvent.Set();
                Shutdown();
                return;
            }

            var cli = CommandLine.Parse(e.Args);
            _paths = cli.DataDirectory is { } dataDirectory ? new AppPaths(dataDirectory) : AppPaths.ForCurrentUser();
            RegisterGlobalExceptionHandlers();

            try
            {
                StartAgent(e.Args);
            }
            catch (Exception ex)
            {
                CrashLog.Write(_paths.LogDirectory, "Startup", ex);
                MessageBox.Show((string)FindResource("Str.UnexpectedError") + Environment.NewLine + ex.Message,
                    (string)FindResource("Str.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private void StartAgent(string[] args)
        {
            var paths = _paths!;
            var cli = CommandLine.Parse(args);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "ClassroomControl.StudentAgent",
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddStudentAgentFileLogger(paths);
            builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
            builder.Services.Configure<AgentOptions>(o =>
            {
                if (cli.AllowLoopback) o.AllowLoopbackTeacher = true;
                if (cli.DiscoveryTarget is { } target) o.DiscoveryTargets = [target];
            });
            // A failing background service must never take the whole agent down; AgentService supervises itself.
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            builder.Services.AddStudentAgentCore(paths);
            builder.Services.AddWindowsPlatform();
            builder.Services.AddSingleton<ILockScreen, WpfLockScreen>();
            builder.Services.AddSingleton<IUserNotifier, WpfUserNotifier>();
            builder.Services.AddSingleton<ITeacherScreenViewer, WpfTeacherScreenViewer>();
            builder.Services.AddSingleton<FeatureIndicatorService>();
            builder.Services.AddSingleton<IUiDispatcher, WpfDispatcher>();
            builder.Services.AddSingleton<IWindowService, WindowService>();
            builder.Services.AddSingleton<ExitCoordinator>();
            builder.Services.AddSingleton<TrayIconService>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddTransient<SettingsViewModel>();
            builder.Services.AddTransient<RegistrationViewModel>();

            _host = builder.Build();
            var services = _host.Services;
            _logger = services.GetRequiredService<ILogger<App>>();

            var settings = services.GetRequiredService<ISettingsService>();
            settings.Load();
            if (args.Contains(StartupService.DisableAutostartArgument, StringComparer.OrdinalIgnoreCase))
                Task.Run(() => settings.UpdateAsync(s => s.StartWithWindows = false)).GetAwaiter().GetResult();
            if (cli.HasSettings)
            {
                // Unattended setup (installer scripts, lab images, CI): the same validation as the Settings window applies.
                Task.Run(() => settings.UpdateAsync(s =>
                {
                    if (cli.ClassroomCode is { } code) s.ClassroomCode = code;
                    if (cli.TeacherAddress is { } address) s.TeacherAddress = address;
                    if (cli.TeacherPort is { } port) s.TeacherPort = port;
                    if (cli.DiscoveryPort is { } discovery) s.DiscoveryPort = discovery;
                    if (cli.StudentName is { } name) s.StudentName = name;
                    if (cli.ComputerName is { } computer) s.ComputerName = computer;
                })).GetAwaiter().GetResult();
            }
            ApplyStartWithWindows(services.GetRequiredService<IStartupService>(), settings.Current.StartWithWindows);

            _host.Start();
            SessionEnding += (_, _) => services.GetRequiredService<ExitCoordinator>().ExitForSessionEnd();
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent!, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => services.GetRequiredService<IWindowService>().ShowMain())), null, Timeout.Infinite, false);

            _indicator = services.GetRequiredService<FeatureIndicatorService>();
            _tray = services.GetRequiredService<TrayIconService>();
            _tray.Start();

            var minimized = args.Contains(StartupService.MinimizedArgument, StringComparer.OrdinalIgnoreCase);
            var windows = services.GetRequiredService<IWindowService>();
            if (!minimized) windows.ShowMain();
            if (!minimized && string.IsNullOrEmpty(settings.Current.ClassroomCode)) windows.ShowRegistration();

            _ = WaitForShutdownAsync(_host);
        }

        private async Task WaitForShutdownAsync(IHost host)
        {
            try
            {
                await host.WaitForShutdownAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected Error during shutdown.");
            }
            finally
            {
                Shutdown();
            }
        }

        private void ApplyStartWithWindows(IStartupService startup, bool enabled)
        {
            try
            {
                if (startup.IsEnabled != enabled) startup.SetEnabled(enabled);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is InvalidOperationException)
            {
                _logger?.LogWarning(ex, "Start with Windows could not be applied.");
            }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                _logger?.LogError(args.Exception, "Unexpected Error on the UI thread.");
                CrashLog.Write(_paths!.LogDirectory, "UI", args.Exception);
                args.Handled = true; // The agent keeps running; the user gets one understandable message.
                if (DateTime.UtcNow - _lastErrorDialog > TimeSpan.FromSeconds(ErrorDialogCooldownSeconds))
                {
                    _lastErrorDialog = DateTime.UtcNow;
                    MessageBox.Show((string)FindResource("Str.UnexpectedError"), (string)FindResource("Str.WindowTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var exception = args.ExceptionObject as Exception;
                _logger?.LogCritical(exception, "Unexpected Error: unhandled exception (terminating: {Terminating}).", args.IsTerminating);
                CrashLog.Write(_paths!.LogDirectory, "AppDomain", exception);
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                _logger?.LogError(args.Exception, "Unexpected Error in a background task.");
                args.SetObserved();
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _showWait?.Unregister(null);
            _indicator?.Dispose();
            _tray?.Dispose();
            _host?.Dispose();
            _showEvent?.Dispose();
            if (_instanceMutex is not null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // This instance never owned the mutex (second launch).
                }
                _instanceMutex.Dispose();
            }
            base.OnExit(e);
        }
    }

    /// <summary>Optional command-line switches for unattended deployment and automated tests. Everything is validated like normal settings.</summary>
    internal sealed class CommandLine
    {
        public string? DataDirectory { get; private set; }
        public string? ClassroomCode { get; private set; }
        public string? TeacherAddress { get; private set; }
        public int? TeacherPort { get; private set; }
        public int? DiscoveryPort { get; private set; }
        public string? StudentName { get; private set; }
        public string? ComputerName { get; private set; }
        public string? DiscoveryTarget { get; private set; }
        public bool AllowLoopback { get; private set; }

        public bool HasSettings => ClassroomCode is not null || TeacherAddress is not null || TeacherPort is not null || DiscoveryPort is not null
            || StudentName is not null || ComputerName is not null;

        public static CommandLine Parse(string[] args)
        {
            var o = new CommandLine();
            string? Next(int i) => i + 1 < args.Length ? args[i + 1] : null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--data-dir": o.DataDirectory = Next(i); break;
                    case "--classroom-code": o.ClassroomCode = Next(i); break;
                    case "--teacher-address": o.TeacherAddress = Next(i); break;
                    case "--teacher-port": o.TeacherPort = int.TryParse(Next(i), out var tp) ? tp : null; break;
                    case "--discovery-port": o.DiscoveryPort = int.TryParse(Next(i), out var dp) ? dp : null; break;
                    case "--student-name": o.StudentName = Next(i); break;
                    case "--computer-name": o.ComputerName = Next(i); break;
                    case "--discovery-target": o.DiscoveryTarget = Next(i); break;
                    case "--allow-loopback": o.AllowLoopback = true; break;
                }
            }
            return o;
        }
    }
}
