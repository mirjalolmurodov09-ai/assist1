using System;
using System.Windows;
using ClassroomControl.StudentAgent.ViewModels;

namespace ClassroomControl.StudentAgent.Views
{
    public partial class RegistrationWindow : Window
    {
        private readonly RegistrationViewModel _viewModel;

        public RegistrationWindow(RegistrationViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            viewModel.CloseRequested += OnCloseRequested;
        }

        private void OnCloseRequested(object? sender, EventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}
