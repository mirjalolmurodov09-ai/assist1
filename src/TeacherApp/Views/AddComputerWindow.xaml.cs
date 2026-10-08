using System;
using System.Windows;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class AddComputerWindow : Window
    {
        private readonly AddComputerViewModel _viewModel;

        public AddComputerWindow(AddComputerViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}
