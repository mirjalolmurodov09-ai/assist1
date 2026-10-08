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
            _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            if (!isFirstInstance)
            {
                _showEvent.Set();
                Shutdown();
                return;
            }

            _paths = AppPaths.ForCurrentUser();
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
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "ClassroomControl.StudentAgent",
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddStudentAgentFileLogger(paths);
            builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
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
}
