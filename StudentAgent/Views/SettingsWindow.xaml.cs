using System;
using System.Windows;
using ClassroomControl.StudentAgent.ViewModels;

namespace ClassroomControl.StudentAgent.Views
{
    public partial class SettingsWindow : Window
    {
        private const int AboutTabIndex = 3;
        private readonly SettingsViewModel _viewModel;

        public SettingsWindow(SettingsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            viewModel.Saved += OnSaved;
        }

        public void SelectAboutTab() => Tabs.SelectedIndex = AboutTabIndex;

        private void OnSaved(object? sender, EventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.Saved -= OnSaved;
            base.OnClosed(e);
        }
    }
}
