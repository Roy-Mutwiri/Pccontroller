using System.Windows;
using TradeFix.Sources.Capture;

namespace TradeFix.Master;

/// <summary>
/// Interaction logic for WindowPickerDialog.xaml
/// </summary>
public partial class WindowPickerDialog : Window
{
    public CapturableWindow? Selected { get; private set; }

    public WindowPickerDialog()
    {
        InitializeComponent();
        LoadWindows();
    }

    private void LoadWindows()
    {
        WindowList.ItemsSource = WindowEnumerator.GetCapturableWindows();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadWindows();

    private void Select_Click(object sender, RoutedEventArgs e) => TrySelectAndClose();

    private void WindowList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => TrySelectAndClose();

    private void TrySelectAndClose()
    {
        if (WindowList.SelectedItem is CapturableWindow window)
        {
            Selected = window;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
