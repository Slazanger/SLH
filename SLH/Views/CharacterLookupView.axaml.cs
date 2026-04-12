using Avalonia.Controls;
using Avalonia.Input;
using SLH.ViewModels;

namespace SLH.Views;

public partial class CharacterLookupView : UserControl
{
    public CharacterLookupView()
    {
        InitializeComponent();
    }

    private void OnSearchNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is CharacterLookupViewModel vm && vm.LookupCommand.CanExecute(null))
            vm.LookupCommand.Execute(null);

        e.Handled = true;
    }
}
