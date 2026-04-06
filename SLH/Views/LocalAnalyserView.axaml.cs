using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SLH.ViewModels;

namespace SLH.Views;

public partial class LocalAnalyserView : UserControl
{
    public LocalAnalyserView()
    {
        InitializeComponent();
        LocalPilotsHost.AddHandler(InputElement.PointerReleasedEvent, OnLocalPilotsHostPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnLocalPilotsHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
            return;
        e.Handled = true;
        if (Resources.TryGetValue("LocalPilotsContextMenu", out var raw) && raw is ContextMenu menu)
            menu.Open(LocalPilotsHost);
    }

    private async void OnPasteLocalClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is null || DataContext is not LocalAnalyserViewModel vm)
            return;
        var text = await top.Clipboard.TryGetTextAsync();
        vm.ApplyLocalText(text);
    }

    private void OnClearLocalClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LocalAnalyserViewModel vm)
            vm.ClearLocalCommand.Execute(null);
    }
}
