using System.Collections.ObjectModel;

namespace SLH.ViewModels;

/// <summary>Host for the shared pilot detail panel (local analyser and character lookup).</summary>
public interface IPilotDetailPanelHost
{
    PilotRowViewModel? PilotDetail { get; }

    ObservableCollection<ActivityHeatmapCellViewModel> ActivityHeatmap { get; }

    string ActivityHeatmapUtcLine { get; }

    string DetailNotes { get; }

    bool ShowPilotDetailNotes { get; }

    /// <summary>When true, show the "Select a pilot" style hint when <see cref="PilotDetail"/> is null.</summary>
    bool ShowEmptyPilotHint { get; }
}
