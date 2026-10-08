using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private MainViewModel ViewModel => (MainViewModel)DataContext;

        /// <summary>Click selects, Ctrl+click adds to the selection, double click opens the full-screen view.</summary>
        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: ComputerViewModel computer }) return;
            if (e.ClickCount == 2)
            {
                ViewModel.SelectOnly(computer);
                if (ViewModel.ViewScreenCommand.CanExecute(null)) ViewModel.ViewScreenCommand.Execute(null);
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ViewModel.ToggleSelection(computer);
            else ViewModel.SelectOnly(computer);
        }

        private void MoveToGroup_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = Loc.Instance["Group.None"], Command = ViewModel.MoveToGroupCommand, CommandParameter = null });
            foreach (var group in ViewModel.Groups)
                menu.Items.Add(new MenuItem { Header = group.Name, Command = ViewModel.MoveToGroupCommand, CommandParameter = group });
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            Application.Current.Shutdown();
        }
    }
}
