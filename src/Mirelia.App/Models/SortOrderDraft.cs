using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

/// <summary>
/// One sort order being edited. Kept apart from the stored row so Cancel
/// really cancels: nothing reaches the library until the editor is saved.
/// </summary>
public sealed partial class SortOrderDraft : ObservableObject
{
    /// <summary>Row id, or null for an order that does not exist yet.</summary>
    public long? Id { get; init; }

    /// <summary>
    /// Display name. Observable so the editor's list reflects a rename as it
    /// is typed rather than on the next rebuild.
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The terms it orders by, most significant first.</summary>
    public List<SortTerm> Terms { get; init; } = [];
}
