using System;
using System.Linq;
using System.Windows;
using ClassroomControl.StudentAgent.ViewModels;
using ClassroomControl.StudentAgent.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ClassroomControl.StudentAgent.Services
{
    /// <summary>Marshals view-model updates onto the WPF UI thread.</summary>
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

    /// <summary>Creates and shows the windows. One instance of each window is kept at a time.</summary>
    internal sealed class WindowService : IWindowService
    {
        private readonly IServiceProvider _services;
        private MainWindow? _main;
        private SettingsWindow? _settings;
        private RegistrationWindow? _registration;

        public WindowService(IServiceProvider services) => _services = services;

        public void ShowMain() => OnUi(() =>
        {
            _main ??= new MainWindow(_services.GetRequiredService<MainViewModel>());
            Present(_main);
        });

        public void ShowSettings(bool aboutTab = false) => OnUi(() =>
        {
            if (_settings is null)
            {
                _settings = new SettingsWindow(_services.GetRequiredService<SettingsViewModel>());
                _settings.Closed += (_, _) => _settings = null;
            }
            if (aboutTab) _settings.SelectAboutTab();
            Present(_settings);
        });

        public void ShowRegistration() => OnUi(() =>
        {
            if (_registration is null)
            {
                _registration = new RegistrationWindow(_services.GetRequiredService<RegistrationViewModel>());
                _registration.Closed += (_, _) => _registration = null;
            }
            Present(_registration);
        });

        public string? PickFileToSend()
        {
            string? chosen = null;
            OnUi(() =>
            {
                try
                {
                    // The dialog belongs to the visible Student window and is brought to the front, so it can never hide behind it.
                    var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                ?? Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = (string)Application.Current.FindResource("Str.SendFile"),
                        CheckFileExists = true,
                        Multiselect = false,
                    };
                    if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true) chosen = dialog.FileName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                {
                    MessageBox.Show(ex.Message, (string)Application.Current.FindResource("Str.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
            return chosen;
        }

        public void OpenFolder(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageBox.Show(ex.Message, (string)Application.Current.FindResource("Str.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void Present(Window window)
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        private static void OnUi(Action action)
        {
            var dispatcher = Application.Current.Dispatcher;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }
    }
}
