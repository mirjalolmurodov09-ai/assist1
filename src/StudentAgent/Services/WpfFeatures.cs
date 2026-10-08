using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClassroomControl.Platform;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.StudentAgent.Features;
using ClassroomControl.Wpf;

namespace ClassroomControl.StudentAgent.Services
{
    internal static class Ui
    {
        public static void Invoke(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        /// <summary>A borderless, always-on-top window covering every monitor.</summary>
        public static Window FullScreenWindow()
        {
            return new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                Background = Brushes.Black,
            };
        }
    }

    /// <summary>"Lock": a black full-screen window with the Teacher's message. While it is up the keys that could hide it are blocked;
    /// Ctrl+Alt+Delete still works (Windows handles it), and the lock is released automatically if the Teacher disappears.</summary>
    internal sealed class WpfLockScreen : ILockScreen
    {
        private readonly KeyboardBlocker _blocker;
        private Window? _window;
        private TextBlock? _text;
        private DispatcherTimer? _keepOnTop;

        public WpfLockScreen(KeyboardBlocker blocker) => _blocker = blocker;

        public void Show(string message) => Ui.Invoke(() =>
        {
            if (_window is null)
            {
                _text = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 40,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 1000,
                };
                _window = Ui.FullScreenWindow();
                _window.Content = _text;
                _window.Closing += (_, e) => e.Cancel = _window is not null && _window.Tag as string != "closing";
                _window.Show();
                _keepOnTop = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) =>
                {
                    if (_window is null) return;
                    _window.Topmost = true;
                    if (!_window.IsActive) _window.Activate();
                }, Dispatcher.CurrentDispatcher);
                _keepOnTop.Start();
                TryEnableBlocker();
            }
            _text!.Text = message;
            _window.Activate();
        });

        public void Hide() => Ui.Invoke(() =>
        {
            _keepOnTop?.Stop();
            _keepOnTop = null;
            _blocker.Disable();
            if (_window is null) return;
            _window.Tag = "closing";
            _window.Close();
            _window = null;
        });

        private void TryEnableBlocker()
        {
            try
            {
                _blocker.Enable();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Keyboard hook unavailable: the window still covers the screen.
            }
        }
    }

    /// <summary>A message from the teacher in a window that stays on top until the student presses OK.</summary>
    internal sealed class WpfUserNotifier : IUserNotifier
    {
        public void Show(string title, string text) => Ui.Invoke(() =>
        {
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = text, FontSize = 20, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(ok);
            var window = new Window
            {
                Title = title,
                Content = panel,
                Width = 520,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            ok.Click += (_, _) => window.Close();
            window.Show();
            window.Activate();
        });
    }

    /// <summary>Shows the teacher's screen full-screen (key frames and changed regions are composed into one bitmap).</summary>
    internal sealed class WpfTeacherScreenViewer : ITeacherScreenViewer
    {
        private readonly KeyboardBlocker _blocker;
        private readonly FrameCompositor _compositor = new FrameCompositor();
        private Window? _window;
        private Image? _image;

        public WpfTeacherScreenViewer(KeyboardBlocker blocker) => _blocker = blocker;

        public void Show(string? title) => Ui.Invoke(() =>
        {
            Close();
            _compositor.Reset();
            _image = new Image { Stretch = Stretch.Uniform };
            _window = Ui.FullScreenWindow();
            _window.Title = string.IsNullOrWhiteSpace(title) ? "O‘qituvchi ekrani" : title;
            _window.Content = _image;
            _window.Closing += (_, e) => e.Cancel = _window is not null && _window.Tag as string != "closing";
            _window.Show();
            try
            {
                _blocker.Enable();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Keyboard hook unavailable: the window is still shown.
            }
        });

        public void Update(ScreenFrameMessage frame)
        {
            var decoded = FrameCompositor.Decode(frame); // decoding happens on the network thread
            if (decoded is null) return;
            Ui.Invoke(() =>
            {
                if (_image is null) return;
                if (_compositor.Apply(decoded)) _image.Source = _compositor.Bitmap;
            });
        }

        public void Close() => Ui.Invoke(() =>
        {
            _blocker.Disable();
            if (_window is null) return;
            _window.Tag = "closing";
            _window.Close();
            _window = null;
            _image = null;
        });
    }

    /// <summary>A red strip across the top of the screen while the teacher controls the mouse and keyboard, so it is never invisible.</summary>
    internal sealed class FeatureIndicatorService : IDisposable
    {
        private readonly FeatureState _state;
        private Window? _banner;

        public FeatureIndicatorService(FeatureState state)
        {
            _state = state;
            _state.Changed += OnChanged;
        }

        private void OnChanged(object? sender, EventArgs e) => Ui.Invoke(() =>
        {
            if (_state.RemoteControl && _banner is null)
            {
                _banner = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ShowActivated = false,
                    Focusable = false,
                    Background = Brushes.Firebrick,
                    Width = 520,
                    Height = 30,
                    Left = (SystemParameters.PrimaryScreenWidth - 520) / 2,
                    Top = 0,
                    Content = new TextBlock
                    {
                        Text = "O‘qituvchi kompyuteringizni boshqarmoqda",
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                _banner.Show();
            }
            else if (!_state.RemoteControl && _banner is not null)
            {
                _banner.Close();
                _banner = null;
            }
        });

        public void Dispose()
        {
            _state.Changed -= OnChanged;
            Ui.Invoke(() =>
            {
                _banner?.Close();
                _banner = null;
            });
        }
    }
}
