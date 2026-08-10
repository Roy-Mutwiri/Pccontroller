using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using TradeFix.Master.Services;
using TradeFix.Master.ViewModels;

namespace TradeFix.Master;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const double MinBoxSize = 24;
    private const double ReferenceWidth = 960;
    private const double ReferenceHeight = 540;

    private bool _isDragging;
    private bool _isResizing;
    private bool _isCropping;
    private string? _cropEdge;
    private SourceItemViewModel? _activeItem;
    private Point _dragStartMouse;
    private double _dragStartX, _dragStartY, _dragStartWidth, _dragStartHeight;
    private double _dragStartCropLeft, _dragStartCropTop, _dragStartCropRight, _dragStartCropBottom;

    /// <summary>Crop handles never let an edge (plus its opposite) exceed this — leaves at least
    /// 10% of the content visible so the box can't be dragged into a degenerate, empty crop.</summary>
    private const double MaxOppositeCropSum = 0.9;

    public MainWindow(MasterHost host)
    {
        InitializeComponent();
        DataContext = new MainViewModel(host, Dispatcher);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void ProgramCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        ViewModel.SelectSource(null);

    private void SceneItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SceneItemViewModel scene)
        {
            ViewModel.SwitchSceneCommand.Execute(scene);
        }
    }

    private void SourceItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SourceItemViewModel item)
        {
            return;
        }

        ViewModel.SelectSource(item);

        _activeItem = item;
        _isDragging = true;
        _dragStartMouse = e.GetPosition(ProgramCanvas);
        _dragStartX = item.X;
        _dragStartY = item.Y;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void SourceItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _activeItem is null)
        {
            return;
        }

        var current = e.GetPosition(ProgramCanvas);
        var deltaX = current.X - _dragStartMouse.X;
        var deltaY = current.Y - _dragStartMouse.Y;

        var newX = Clamp(_dragStartX + deltaX, 0, ReferenceWidth - _activeItem.Width);
        var newY = Clamp(_dragStartY + deltaY, 0, ReferenceHeight - _activeItem.Height);

        ViewModel.UpdateSourceTransform(_activeItem, newX, newY, _activeItem.Width, _activeItem.Height);
    }

    private void SourceItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _activeItem = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SourceItemViewModel item)
        {
            return;
        }

        ViewModel.SelectSource(item);

        _activeItem = item;
        _isResizing = true;
        _dragStartMouse = e.GetPosition(ProgramCanvas);
        _dragStartWidth = item.Width;
        _dragStartHeight = item.Height;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing || _activeItem is null)
        {
            return;
        }

        var current = e.GetPosition(ProgramCanvas);
        var deltaX = current.X - _dragStartMouse.X;
        var deltaY = current.Y - _dragStartMouse.Y;

        var newWidth = Clamp(_dragStartWidth + deltaX, MinBoxSize, ReferenceWidth - _activeItem.X);
        var newHeight = Clamp(_dragStartHeight + deltaY, MinBoxSize, ReferenceHeight - _activeItem.Y);

        ViewModel.UpdateSourceTransform(_activeItem, _activeItem.X, _activeItem.Y, newWidth, newHeight);
    }

    private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isResizing = false;
        _activeItem = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void CropHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (element.DataContext is not SourceItemViewModel item || element.Tag is not string edge)
        {
            return;
        }

        ViewModel.SelectSource(item);

        _activeItem = item;
        _isCropping = true;
        _cropEdge = edge;
        _dragStartMouse = e.GetPosition(ProgramCanvas);
        _dragStartCropLeft = item.CropLeft;
        _dragStartCropTop = item.CropTop;
        _dragStartCropRight = item.CropRight;
        _dragStartCropBottom = item.CropBottom;
        element.CaptureMouse();
        e.Handled = true;
    }

    private void CropHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCropping || _activeItem is null)
        {
            return;
        }

        var current = e.GetPosition(ProgramCanvas);
        var deltaX = current.X - _dragStartMouse.X;
        var deltaY = current.Y - _dragStartMouse.Y;

        var left = _dragStartCropLeft;
        var top = _dragStartCropTop;
        var right = _dragStartCropRight;
        var bottom = _dragStartCropBottom;

        switch (_cropEdge)
        {
            case "Left":
                left = Clamp(_dragStartCropLeft + deltaX / _activeItem.Width, 0, MaxOppositeCropSum - right);
                break;
            case "Right":
                right = Clamp(_dragStartCropRight - deltaX / _activeItem.Width, 0, MaxOppositeCropSum - left);
                break;
            case "Top":
                top = Clamp(_dragStartCropTop + deltaY / _activeItem.Height, 0, MaxOppositeCropSum - bottom);
                break;
            case "Bottom":
                bottom = Clamp(_dragStartCropBottom - deltaY / _activeItem.Height, 0, MaxOppositeCropSum - top);
                break;
        }

        ViewModel.UpdateSourceCrop(_activeItem, left, top, right, bottom);
    }

    private void CropHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isCropping = false;
        _cropEdge = null;
        _activeItem = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
