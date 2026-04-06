using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SLH.ViewModels;

namespace SLH.Views;

public partial class LocalAnalyserView : UserControl
{
    public LocalAnalyserView()
    {
        InitializeComponent();
        LocalPilotsHost.AddHandler(InputElement.PointerReleasedEvent, OnLocalPilotsHostPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Bubble: keys from the focused ListBox / ListBoxItem bubble up through this UserControl.
        // TabControl does not place tab content under TabItem in the visual tree, so TopLevel tunnel
        // and TabItem ancestors are unreliable; TabControl.SelectedContent identifies the active tab.
        AddHandler(InputElement.KeyDownEvent, OnPasteHotKeyBubble, RoutingStrategies.Bubble);

        LocalPilotsHost.PointerPressed += OnLocalPilotsHostPointerPressed;
    }

    private void OnLocalPilotsHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled)
            return;
        LocalPilotsList.Focus();
    }

    private void OnPasteHotKeyBubble(object? sender, KeyEventArgs e)
    {
        if (!IsThisLocalTabSelectedContent())
            return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (!IsPasteLocalGesture(e))
            return;

        e.Handled = true;
        var clearFirst = IsCtrlVPasteGesture(e);
        _ = PasteLocalFromClipboardAsync(clearFirst);
    }

    private static bool IsPasteLocalGesture(KeyEventArgs e) =>
        e.Key is Key.P or Key.V
        || e.PhysicalKey is PhysicalKey.P or PhysicalKey.V;

    private static bool IsCtrlVPasteGesture(KeyEventArgs e) =>
        e.Key is Key.V || e.PhysicalKey is PhysicalKey.V;

    /// <summary>True when this view is the tab control's selected content (tab content lives in a presenter, not under TabItem visually).</summary>
    private bool IsThisLocalTabSelectedContent()
    {
        foreach (var a in this.GetVisualAncestors())
        {
            if (a is TabControl tc)
                return ReferenceEquals(tc.SelectedContent, this);
        }

        return true;
    }

    private async Task PasteLocalFromClipboardAsync(bool clearBeforePaste = false)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || top.Clipboard is null || DataContext is not LocalAnalyserViewModel vm)
            return;
        var text = await top.Clipboard.TryGetTextAsync();
        if (clearBeforePaste)
            vm.ClearLocalCommand.Execute(null);
        vm.ApplyLocalText(text);
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
        vm.ClearLocalCommand.Execute(null);
        vm.ApplyLocalText(text);
    }

    private void OnClearLocalClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LocalAnalyserViewModel vm)
            vm.ClearLocalCommand.Execute(null);
    }

    private void OnLocalPilotsDoubleTapped(object? sender, RoutedEventArgs _)
    {
        if (DataContext is not LocalAnalyserViewModel vm)
            return;

        if (vm.SelectedPilot?.CharacterId is not { } id || id <= 0)
            return;

        var url = $"https://zkillboard.com/character/{id}/";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // No default browser or shell — ignore
        }
    }

}
