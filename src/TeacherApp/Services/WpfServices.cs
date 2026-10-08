using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ClassroomControl.Wpf;
using ClassroomControl.TeacherApp.Views;

namespace ClassroomControl.TeacherApp.Services
{
    internal sealed class WpfDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
    }

    internal sealed class ThemeService : IThemeService
    {
        public void Apply(string theme)
        {
            var uri = new Uri(theme == "Dark" ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
            var merged = Application.Current.Resources.MergedDictionaries;
            merged[0] = new ResourceDictionary { Source = uri };
        }
    }

    /// <summary>Decodes incoming frames off the UI thread and keeps one bitmap per computer for its card and its full-screen window.</summary>
    internal sealed class PreviewService : IDisposable
    {
        private readonly MonitoringService _monitoring;
        private readonly Server _server;
        private readonly Func<string, ComputerViewModel?> _find;
        private readonly ConcurrentDictionary<string, FrameCompositor> _compositors = new();

        public PreviewService(MonitoringService monitoring, Server server, Func<string, ComputerViewModel?> find)
        {
            _monitoring = monitoring;
            _server = server;
            _find = find;
            _monitoring.FrameArrived += OnFrame;
            _server.DeviceChanged += OnDeviceChanged;
        }

        private void OnFrame(object? sender, FrameReceivedEventArgs e)
        {
            var decoded = FrameCompositor.Decode(e.Frame);
            if (decoded is null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => Apply(e.DeviceId, decoded)));
        }

        private void Apply(string deviceId, DecodedRegion decoded)
        {
            var compositor = _compositors.GetOrAdd(deviceId, _ => new FrameCompositor());
            if (!compositor.Apply(decoded)) return;
            var vm = _find(deviceId);
            if (vm is not null && !ReferenceEquals(vm.Preview, compositor.Bitmap)) vm.Preview = compositor.Bitmap;
        }

        private void OnDeviceChanged(object? sender, DeviceSnapshot snapshot)
        {
            if (!snapshot.Online && _compositors.TryRemove(snapshot.DeviceId, out _)) { /* a reconnecting computer starts with a key frame */ }
        }

        public void Dispose()
        {
            _monitoring.FrameArrived -= OnFrame;
            _server.DeviceChanged -= OnDeviceChanged;
        }
    }

    internal sealed class DialogService : IDialogService
    {
        private readonly Server _server;
        private readonly UserService _users;
        private readonly TeacherSession _session;
        private readonly MonitoringService _monitoring;
        private readonly IUiDispatcher _ui;
        private readonly IThemeService _theme;
        private Window? _settings, _shots, _add, _about;

        public DialogService(Server server, UserService users, TeacherSession session, MonitoringService monitoring, IUiDispatcher ui, IThemeService theme)
        {
            _server = server;
            _users = users;
            _session = session;
            _monitoring = monitoring;
            _ui = ui;
            _theme = theme;
        }

        private static Window? Owner => Application.Current?.MainWindow;

        public Task<bool> ConfirmAsync(string title, string message)
        {
            var answer = MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return Task.FromResult(answer == MessageBoxResult.Yes);
        }

        public Task<string?> PromptAsync(string title, string label, string initial, bool multiline)
        {
            var window = new PromptWindow(title, label, initial, multiline) { Owner = Owner };
            return Task.FromResult(window.ShowDialog() == true ? window.Value : null);
        }

        public void Info(string title, string message) => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public void ShowFullScreen(ComputerViewModel computer, bool withRemoteControl)
        {
            var window = new FullScreenWindow(new FullScreenViewModel(computer, _server, _monitoring), withRemoteControl) { Owner = Owner };
            window.Show();
        }

        public void ShowSettings(SettingsTab tab) => ShowSingle(ref _settings, () => new SettingsWindow(new SettingsViewModel(_server, _users, _session, _monitoring, this, _ui, _theme), tab),
            existing => ((SettingsWindow)existing).SelectTab(tab));

        public void ShowScreenshots() => ShowSingle(ref _shots, () => new ScreenshotHistoryWindow(new ScreenshotHistoryViewModel(_server, this)), _ => { });
        public void ShowAbout() => ShowSingle(ref _about, () => new AboutWindow(), _ => { });
        public void ShowAddComputer() => ShowSingle(ref _add, () => new AddComputerWindow(new AddComputerViewModel(_server, _ui)), _ => { });

        private static void ShowSingle(ref Window? slot, Func<Window> create, Action<Window> reuse)
        {
            if (slot is { IsLoaded: true })
            {
                reuse(slot);
                slot.Activate();
                return;
            }
            var window = create();
            window.Owner = Owner;
            var captured = window;
            slot = captured;
            window.Show();
        }

        public void OpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Info(Loc.Instance["Error.Title"], ex.Message);
            }
        }
    }
}
