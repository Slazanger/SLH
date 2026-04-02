using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLH.Services;

namespace SLH.ViewModels;

public partial class DscanRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _typeName = "";
    [ObservableProperty] private string _distance = "";
    [ObservableProperty] private string _category = "";
}

public partial class DscanViewModel : ObservableObject
{
    [ObservableProperty] private string _pasteText = "";

    public ObservableCollection<DscanRowViewModel> Rows { get; } = new();

    [RelayCommand]
    private void Analyze()
    {
        Rows.Clear();
        foreach (var e in DscanParser.Parse(PasteText))
        {
            Rows.Add(new DscanRowViewModel
            {
                Name = e.Name,
                TypeName = e.TypeName,
                Distance = e.Distance,
                Category = string.IsNullOrWhiteSpace(e.TypeName) ? "Other" : DscanClassifier.Classify(e.TypeName)
            });
        }
    }
}
