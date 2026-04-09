using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SLH.Models;
using SLH.ViewModels;

namespace SLH.Views;

public partial class MainWindow : Window
{
    private const double RootPaddingTotal = 8;

    private MainWindowViewModel? _uiScaleListener;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownLocalPasteFallback, RoutingStrategies.Bubble);
        Resized += (_, _) => UpdateScaledHostLogicalSize();
        SizeChanged += (_, _) => UpdateScaledHostLogicalSize();
        Opened += OnWindowOpened;
        Closing += (_, _) => DetachUiScaleListener();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        AttachUiScaleListener();
        UpdateScaledHostLogicalSize();
    }

    private void AttachUiScaleListener()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (ReferenceEquals(_uiScaleListener, vm))
            return;
        DetachUiScaleListener();
        _uiScaleListener = vm;
        vm.PropertyChanged += OnMainVmPropertyChanged;
    }

    private void DetachUiScaleListener()
    {
        if (_uiScaleListener is null)
            return;
        _uiScaleListener.PropertyChanged -= OnMainVmPropertyChanged;
        _uiScaleListener = null;
    }

    private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.UiScale))
            UpdateScaledHostLogicalSize();
    }

    /// <summary>
    /// Logical size = (client − padding) / scale so that after RenderTransform scale, content fills the client.
    /// </summary>
    private void UpdateScaledHostLogicalSize()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        var scale = Math.Clamp(vm.UiScale, AppSettings.UiScaleMin, AppSettings.UiScaleMax);

        var cw = ClientSize.Width - RootPaddingTotal;
        var ch = ClientSize.Height - RootPaddingTotal;
        if (cw < 0)
            cw = 0;
        if (ch < 0)
            ch = 0;

        if (ScaledHost is null)
            return;
        ScaledHost.Width = cw / scale;
        ScaledHost.Height = ch / scale;
    }

    /// <summary>
    /// When focus never reaches the local list (e.g. tab strip), still paste if Local tab is selected.
    /// Skips if a child already handled the key or focus is in a TextBox.
    /// </summary>
    private async void OnWindowKeyDownLocalPasteFallback(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedTabIndex != 0)
            return;
        if (e.Source is TextBox)
            return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (!IsLocalPasteGesture(e))
            return;

        e.Handled = true;
        if (Clipboard is null)
            return;
        var text = await Clipboard.TryGetTextAsync();
        if (IsCtrlVPasteGesture(e))
            vm.Local.ClearLocalCommand.Execute(null);
        vm.Local.ApplyLocalText(text);
    }

    private static bool IsLocalPasteGesture(KeyEventArgs e) =>
        e.Key is Key.P or Key.V
        || e.PhysicalKey is PhysicalKey.P or PhysicalKey.V;

    private static bool IsCtrlVPasteGesture(KeyEventArgs e) =>
        e.Key is Key.V || e.PhysicalKey is PhysicalKey.V;
}