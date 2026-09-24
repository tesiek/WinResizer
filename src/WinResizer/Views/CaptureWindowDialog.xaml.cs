using System.Windows;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class CaptureWindowDialog : Window
{
    private readonly CaptureWindowViewModel _viewModel;

    public CaptureWindowDialog() : this(new CaptureWindowSource()) { }

    internal CaptureWindowDialog(ICaptureWindowSource source)
    {
        InitializeComponent();
        _viewModel = new CaptureWindowViewModel(source);
        DataContext = _viewModel;
        if (_viewModel.Windows.Count > 0)
            _viewModel.SelectedWindow = _viewModel.Windows[0];
    }

    internal CapturedWindow? Result { get; private set; }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCaptureSelected(out var result)) return;
        Result = result;
        DialogResult = true;
    }
}
