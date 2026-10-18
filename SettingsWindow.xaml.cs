using System.Windows;
using System.Windows.Input;

namespace UsageWidget;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        ApiKeyBox.Text = _settings.ApiKey;
        AccountBox.Text = _settings.AccountNumber;
        GasMpanBox.Text = _settings.GasMpan;
        ElectricMpanBox.Text = _settings.ElectricMpan;
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        _settings.ApiKey = ApiKeyBox.Text.Trim();
        _settings.AccountNumber = AccountBox.Text.Trim();
        _settings.GasMpan = GasMpanBox.Text.Trim();
        _settings.ElectricMpan = ElectricMpanBox.Text.Trim();

        _settings.Save();
        Status.Text = $"Saved to {AppSettings.FilePath}";
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
