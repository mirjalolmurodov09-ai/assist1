using System.ComponentModel;
using System.Windows;
using ClassroomControl.StudentAgent.ViewModels;

namespace ClassroomControl.StudentAgent.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        /// <summary>Closing the window only hides it; the agent keeps running in the tray until Exit is chosen there.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
        }
    }
}
