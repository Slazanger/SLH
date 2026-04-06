using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SLH.ViewModels;

namespace SLH.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownLocalPasteFallback, RoutingStrategies.Bubble);
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