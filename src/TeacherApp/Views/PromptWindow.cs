using System.Windows;
using System.Windows.Controls;

namespace ClassroomControl.TeacherApp.Views
{
    /// <summary>One-field input dialog (built in code: no XAML needed for a label, a text box and two buttons).</summary>
    internal sealed class PromptWindow : Window
    {
        private readonly TextBox _input;

        public PromptWindow(string title, string label, string initial, bool multiline)
        {
            Title = title;
            SetResourceReference(StyleProperty, typeof(Window));
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _input = new TextBox
            {
                Text = initial,
                Margin = new Thickness(0, 6, 0, 14),
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 90 : 0,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                MaxLength = 2000,
            };
            var ok = new Button { Content = Loc.Instance["Common.Ok"], IsDefault = !multiline, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => DialogResult = true;
            var cancel = new Button { Content = Loc.Instance["Common.Cancel"], IsCancel = true, MinWidth = 90, Margin = new Thickness(0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_input);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, _) =>
            {
                _input.Focus();
                _input.SelectAll();
            };
        }

        public string Value => _input.Text;
    }
}
