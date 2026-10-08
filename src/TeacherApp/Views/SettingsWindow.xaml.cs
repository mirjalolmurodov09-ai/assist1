using System;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomControl.TeacherApp.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsViewModel _viewModel;

        public SettingsWindow(SettingsViewModel viewModel, SettingsTab tab)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            Tabs.SelectedIndex = (int)tab;
        }

        public void SelectTab(SettingsTab tab) => Tabs.SelectedIndex = (int)tab;

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string code }) _viewModel.Language = code;
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}
