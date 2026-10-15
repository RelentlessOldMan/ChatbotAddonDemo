using System.Windows;
using Demo.Desktop.ViewModels;

namespace Demo.Desktop;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveKey(KeyBox.Password);
        KeyBox.Clear(); // Don't keep the secret in the control longer than necessary.
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Save()) DialogResult = true; // otherwise the limit box shows what's wrong
    }
}
