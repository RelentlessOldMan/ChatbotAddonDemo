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
            var settings = new SettingsViewModel(services);
            var dialog = new SettingsWindow(settings) { Owner = this };
            App.MakeUnobtrusive(dialog);
            // A key saved or removed in the dialog is already in effect, even if the dialog is then cancelled.
            if (dialog.ShowDialog() == true || settings.CredentialsChanged) viewModel.ApplySettings(settings.CredentialsChanged);
        };
    }
}
