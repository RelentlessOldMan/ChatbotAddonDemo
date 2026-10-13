using System.Windows;
using Demo.Desktop.ViewModels;

namespace Demo.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(AppServices services, MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SettingsRequested += () =>
        {
            var dialog = new SettingsWindow(new SettingsViewModel(services)) { Owner = this };
            if (dialog.ShowDialog() == true) viewModel.ApplySettings();
        };
    }
}
