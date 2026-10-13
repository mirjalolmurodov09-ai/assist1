using System.Windows;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class RecoverPasswordWindow : Window
    {
        private readonly RecoverPasswordViewModel _viewModel;

        public RecoverPasswordWindow(RecoverPasswordViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var ok = _viewModel.TryReset(Password.Password, Confirm.Password);
            Password.Clear();
            Confirm.Clear();
            if (ok) DialogResult = true;
        }
    }
}
