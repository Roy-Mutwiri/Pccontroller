using System.Windows;
using System.Windows.Input;
using TradeFix.Launcher.Services;

namespace TradeFix.Launcher;

/// <summary>
/// Interaction logic for RoleSelectionWindow.xaml
/// </summary>
public partial class RoleSelectionWindow : Window
{
    public LauncherRole SelectedRole { get; private set; } = LauncherRole.Unset;

    public RoleSelectionWindow()
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDarkTitleBar(this);
    }

    private void MasterCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Choose(LauncherRole.Master);
    private void MasterButton_Click(object sender, RoutedEventArgs e) => Choose(LauncherRole.Master);
    private void RenderNodeCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Choose(LauncherRole.RenderNode);
    private void RenderNodeButton_Click(object sender, RoutedEventArgs e) => Choose(LauncherRole.RenderNode);

    private void Choose(LauncherRole role)
    {
        SelectedRole = role;
        DialogResult = true;
    }
}
