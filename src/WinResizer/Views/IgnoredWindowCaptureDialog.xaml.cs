using System;
using System.Windows;
using WinResizer.Presentation;
using WinResizer.Services;

namespace WinResizer.Views;

public partial class IgnoredWindowCaptureDialog : Window
{
    private readonly IgnoredWindowCaptureViewModel _viewModel;
    internal IgnoredWindowCaptureDialog(Action<IgnoredWindowCaptureItem> save) : this(new IgnoredWindowCaptureSource(), save) { }
    internal IgnoredWindowCaptureDialog(IIgnoredWindowCaptureSource source, Action<IgnoredWindowCaptureItem> save)
    {
        InitializeComponent();
        _viewModel = new IgnoredWindowCaptureViewModel(source, save);
        DataContext = _viewModel;
        if (_viewModel.Windows.Count > 0) _viewModel.SelectedWindow = _viewModel.Windows[0];
    }
    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryCaptureSelected()) DialogResult = true;
    }
}
