using System.Windows;
using System.Windows.Input;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class ScreenshotHistoryWindow : Window
    {
        public ScreenshotHistoryWindow(ScreenshotHistoryViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var vm = (ScreenshotHistoryViewModel)DataContext;
            if (vm.OpenCommand.CanExecute(null)) vm.OpenCommand.Execute(null);
        }
    }
}
