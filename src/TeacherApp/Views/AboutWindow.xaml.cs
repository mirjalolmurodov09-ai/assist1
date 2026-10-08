using System.Windows;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            DataContext = new AboutViewModel();
        }
    }
}
