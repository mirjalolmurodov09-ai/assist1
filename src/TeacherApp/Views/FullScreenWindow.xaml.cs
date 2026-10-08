using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class FullScreenWindow : Window
    {
        private readonly FullScreenViewModel _viewModel;

        public FullScreenWindow(FullScreenViewModel viewModel, bool startRemoteControl)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            Loaded += async (_, _) =>
            {
                await _viewModel.OpenAsync();
                if (startRemoteControl) await _viewModel.StartRemoteAsync();
            };
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override async void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            await _viewModel.CloseAsync();
        }

        /// <summary>Normalizes a mouse position to 0..1 of the displayed screen image (letterboxing excluded). Null when outside the image.</summary>
        private (double X, double Y)? Normalize(MouseEventArgs e)
        {
            if (ScreenImage.Source is not BitmapSource bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return null;
            var position = e.GetPosition(ScreenImage);
            var scale = Math.Min(ScreenImage.ActualWidth / bitmap.PixelWidth, ScreenImage.ActualHeight / bitmap.PixelHeight);
            var shownWidth = bitmap.PixelWidth * scale;
            var shownHeight = bitmap.PixelHeight * scale;
            var x = (position.X - (ScreenImage.ActualWidth - shownWidth) / 2) / shownWidth;
            var y = (position.Y - (ScreenImage.ActualHeight - shownHeight) / 2) / shownHeight;
            return x is < 0 or > 1 || y is < 0 or > 1 ? null : (x, y);
        }

        private async void Screen_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_viewModel.RemoteActive || Normalize(e) is not { } p) return;
            await _viewModel.MouseMoveAsync(p.X, p.Y);
        }

        private async void Screen_MouseButton(object sender, MouseButtonEventArgs e)
        {
            if (!_viewModel.RemoteActive || Normalize(e) is not { } p) return;
            var button = e.ChangedButton switch
            {
                MouseButton.Left => MouseButtonKind.Left,
                MouseButton.Right => MouseButtonKind.Right,
                MouseButton.Middle => MouseButtonKind.Middle,
                _ => MouseButtonKind.None,
            };
            if (button == MouseButtonKind.None) return;
            await _viewModel.MouseButtonAsync(p.X, p.Y, button, e.ButtonState == MouseButtonState.Pressed);
            e.Handled = true;
        }

        private async void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_viewModel.RemoteActive || Normalize(e) is not { } p) return;
            await _viewModel.MouseWheelAsync(p.X, p.Y, e.Delta);
            e.Handled = true;
        }

        /// <summary>While remote control is on, keys go to the student. Ctrl+Shift+F12 always switches remote control off again.</summary>
        private async void Window_PreviewKey(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (e.IsDown && key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (_viewModel.ToggleRemoteCommand.CanExecute(null) && _viewModel.RemoteActive) _viewModel.ToggleRemoteCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (!_viewModel.RemoteActive) return;
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0) return;
            await _viewModel.KeyAsync(virtualKey, e.IsDown, IsExtended(key));
            e.Handled = true;
        }

        private static bool IsExtended(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.RightCtrl or Key.RightAlt or Key.Divide or Key.NumLock or Key.PrintScreen;
    }
}
