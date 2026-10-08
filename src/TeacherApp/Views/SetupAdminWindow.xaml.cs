using System.Windows;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class SetupAdminWindow : Window
    {
        private readonly SetupAdminViewModel _viewModel;

        public SetupAdminWindow(SetupAdminViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        public UserRecord? User { get; private set; }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            User = _viewModel.Create(Password.Password, Confirm.Password);
            if (User is not null) DialogResult = true;
        }
    }
}
