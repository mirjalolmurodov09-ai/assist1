using System.Windows;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class LoginWindow : Window
    {
        private readonly LoginViewModel _viewModel;

        public LoginWindow(LoginViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            Loaded += (_, _) => Password.Focus();
        }

        public UserRecord? User { get; private set; }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            User = _viewModel.TryLogin(Password.Password);
            Password.Clear();
            if (User is not null) DialogResult = true;
        }
    }
}
