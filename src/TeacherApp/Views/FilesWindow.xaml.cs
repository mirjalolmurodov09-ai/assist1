using System.Windows;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class FilesWindow : Window
    {
        public FilesWindow(FileTransferViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
