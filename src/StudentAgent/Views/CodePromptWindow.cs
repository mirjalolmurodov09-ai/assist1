using System.Windows;
using System.Windows.Controls;

namespace ClassroomControl.StudentAgent.Views
{
    /// <summary>Small modal prompt (built in code: it has no XAML because it only hosts one text box).</summary>
    internal sealed class CodePromptWindow : Window
    {
        private readonly TextBox _input = new() { Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(4), MaxLength = 32 };

        public CodePromptWindow(string title, string message, string okText, string cancelText)
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var ok = new Button { Content = okText, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => DialogResult = true;
            var cancel = new Button { Content = cancelText, IsCancel = true };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_input);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, _) => _input.Focus();
        }

        public string EnteredText => _input.Text;
    }
}
