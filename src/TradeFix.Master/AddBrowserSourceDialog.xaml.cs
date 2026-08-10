using System.Windows;

namespace TradeFix.Master;

/// <summary>
/// Interaction logic for AddBrowserSourceDialog.xaml
/// </summary>
public partial class AddBrowserSourceDialog : Window
{
    public string? Url { get; private set; }

    public AddBrowserSourceDialog()
    {
        InitializeComponent();
        UrlBox.Focus();
        UrlBox.CaretIndex = UrlBox.Text.Length;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var text = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || text == "https://")
        {
            MessageBox.Show(this, "Enter a website URL first.", "Add Browser Source", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!text.Contains("://"))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "That doesn't look like a valid http(s) URL.", "Add Browser Source", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Url = uri.ToString();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
