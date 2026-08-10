using System.Collections.ObjectModel;
using System.Windows;

namespace TradeFix.Setup;

/// <summary>
/// Interaction logic for MainWindow.xaml — runs install or uninstall on a background thread as
/// soon as it loads (no extra button click needed, matching the previous PowerShell installer's
/// "just runs" behavior), showing progress as a simple log list.
/// </summary>
public partial class MainWindow : Window
{
    private readonly bool _isUninstall;
    private readonly ObservableCollection<string> _logLines = [];

    public MainWindow(bool isUninstall)
    {
        InitializeComponent();
        _isUninstall = isUninstall;
        LogList.ItemsSource = _logLines;

        TitleText.Text = isUninstall ? "TradeFix Broadcast — Uninstall" : "TradeFix Broadcast — Setup";
        SubtitleText.Text = isUninstall
            ? "Removing TradeFix Broadcast from this PC."
            : "Installing TradeFix Broadcast for the current user — no admin rights needed.";

        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        void Log(string message) => Dispatcher.Invoke(() => _logLines.Add(message));

        // This is called from a "Loaded += async (_, _) => ..." event handler, which behaves like
        // async void: an exception escaping here can't be caught by any caller and crashes the
        // whole process with no visible explanation to the operator — exactly what happened during
        // real end-to-end testing (a COM apartment-threading bug in shortcut creation, since fixed
        // in Installer.CreateShortcuts, but this try/catch stays as defense-in-depth against
        // whatever the next unexpected failure turns out to be).
        try
        {
            var outcome = await Task.Run(() => _isUninstall ? Installer.Uninstall(Log) : Installer.Install(Log));

            Log(outcome.Message);
            if (!outcome.Success)
            {
                MessageBox.Show(this, outcome.Message, "TradeFix Broadcast Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (outcome.Success && !_isUninstall)
            {
                // give the operator a moment to see "Setup complete" before auto-closing
                await Task.Delay(1500);
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}");
            MessageBox.Show(this,
                $"Setup hit an unexpected error and couldn't finish:\n\n{ex.Message}\n\nNo changes made after this point.",
                "TradeFix Broadcast Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        CloseButton.IsEnabled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
