using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Mirelia.App.Models;
using Mirelia.App.ViewModels;
using Mirelia.Library.Database;
using Mirelia.Library.Querying;

namespace Mirelia.App.Dialogs;

/// <summary>
/// Edits the library's user-defined sort orders: their names, their terms
/// and the order they appear in. The built-in orders are not shown — they
/// are the same in every library and cannot be changed.
/// </summary>
public partial class SortOrderEditorDialog : Window
{
    /// <summary>A sortable field with its display label.</summary>
    private sealed record FieldChoice(SortField Field)
    {
        public override string ToString() => Field.Label;
    }

    /// <summary>A direction with its display label.</summary>
    private sealed record DirectionChoice(bool Descending, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly FieldChoice[] Fields =
        [.. SortFields.All.Select(f => new FieldChoice(f))];

    private static readonly DirectionChoice[] Directions =
    [
        new(false, "Ascending"),
        new(true, "Descending"),
    ];

    private readonly MainViewModel? _viewModel;
    private readonly List<SortOrderDraft> _drafts = [];
    private readonly List<long> _deleted = [];
    private SortOrderDraft? _current;
    private bool _loading;

    /// <summary>XAML-loader constructor.</summary>
    public SortOrderEditorDialog()
    {
        InitializeComponent();
    }

    /// <summary>Creates the dialog over the library's stored sort orders.</summary>
    /// <param name="viewModel">Owns the library and applies the result on Save.</param>
    public SortOrderEditorDialog(MainViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        foreach (SortOrderInfo order in viewModel.CustomSortOrders)
        {
            _drafts.Add(new SortOrderDraft
            {
                Id = order.Id,
                Name = order.Name,
                Terms = [.. order.Spec.Terms],
            });
        }

        OrderList.ItemsSource = _drafts;
        if (_drafts.Count > 0)
        {
            OrderList.SelectedIndex = 0;
        }
    }

    private void OnOrderSelected(object? sender, SelectionChangedEventArgs e)
    {
        _current = OrderList.SelectedItem as SortOrderDraft;
        EditorPanel.IsVisible = _current is not null;
        EmptyHint.IsVisible = _current is null;
        if (_current is null)
        {
            return;
        }

        _loading = true;
        try
        {
            NameBox.Text = _current.Name;
        }
        finally
        {
            _loading = false;
        }

        RebuildTerms();
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_loading && _current is not null)
        {
            _current.Name = NameBox.Text ?? string.Empty;
        }
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        var draft = new SortOrderDraft
        {
            Name = UniqueName(),
            Terms = [new SortTerm(SortFields.All[0].Key, Descending: false)],
        };
        _drafts.Add(draft);
        OrderList.ItemsSource = null;
        OrderList.ItemsSource = _drafts;
        OrderList.SelectedItem = draft;
        NameBox.Focus();
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_current is not { } draft)
        {
            return;
        }

        if (draft.Id is { } id)
        {
            _deleted.Add(id);
        }

        int index = _drafts.IndexOf(draft);
        _drafts.Remove(draft);
        OrderList.ItemsSource = null;
        OrderList.ItemsSource = _drafts;
        OrderList.SelectedIndex = Math.Min(index, _drafts.Count - 1);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => MoveCurrent(-1);

    private void OnMoveDown(object? sender, RoutedEventArgs e) => MoveCurrent(1);

    private void MoveCurrent(int delta)
    {
        if (_current is not { } draft)
        {
            return;
        }

        int from = _drafts.IndexOf(draft);
        int to = from + delta;
        if (to < 0 || to >= _drafts.Count)
        {
            return;
        }

        _drafts.RemoveAt(from);
        _drafts.Insert(to, draft);
        OrderList.ItemsSource = null;
        OrderList.ItemsSource = _drafts;
        OrderList.SelectedItem = draft;
    }

    private void OnAddTerm(object? sender, RoutedEventArgs e)
    {
        if (_current is not { } draft)
        {
            return;
        }

        CaptureTerms();
        draft.Terms.Add(new SortTerm(SortFields.All[0].Key, Descending: false));
        RebuildTerms();
    }

    /// <summary>
    /// Rebuilds the term rows from the draft. Structural edits go through
    /// here rather than patching the panel, so row indices and the draft
    /// cannot drift apart.
    /// </summary>
    private void RebuildTerms()
    {
        TermsPanel.Children.Clear();
        if (_current is not { } draft)
        {
            return;
        }

        for (int i = 0; i < draft.Terms.Count; i++)
        {
            TermsPanel.Children.Add(BuildTermRow(draft.Terms[i], i));
        }
    }

    private Control BuildTermRow(SortTerm term, int index)
    {
        var fieldBox = new ComboBox
        {
            ItemsSource = Fields,
            SelectedItem = Fields.FirstOrDefault(f => f.Field.Key == term.Field) ?? Fields[0],
            Width = 200,
        };
        var dirBox = new ComboBox
        {
            ItemsSource = Directions,
            SelectedItem = Directions.First(d => d.Descending == term.Descending),
            Width = 130,
        };

        var up = new Button { Content = "Up", Padding = new Avalonia.Thickness(7, 3) };
        up.Click += (_, _) => MoveTerm(index, -1);
        var down = new Button { Content = "Down", Padding = new Avalonia.Thickness(7, 3) };
        down.Click += (_, _) => MoveTerm(index, 1);
        var remove = new Button { Content = "Remove", Padding = new Avalonia.Thickness(7, 3) };
        remove.Click += (_, _) => RemoveTerm(index);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        row.Children.Add(fieldBox);
        row.Children.Add(dirBox);
        row.Children.Add(up);
        row.Children.Add(down);
        row.Children.Add(remove);
        return row;
    }

    private void MoveTerm(int index, int delta)
    {
        if (_current is not { } draft)
        {
            return;
        }

        int to = index + delta;
        if (to < 0 || to >= draft.Terms.Count)
        {
            return;
        }

        CaptureTerms();
        (draft.Terms[index], draft.Terms[to]) = (draft.Terms[to], draft.Terms[index]);
        RebuildTerms();
    }

    private void RemoveTerm(int index)
    {
        if (_current is not { } draft)
        {
            return;
        }

        CaptureTerms();
        draft.Terms.RemoveAt(index);
        RebuildTerms();
    }

    /// <summary>Reads the term rows back into the draft before it is restructured or saved.</summary>
    private void CaptureTerms()
    {
        if (_current is not { } draft)
        {
            return;
        }

        for (int i = 0; i < TermsPanel.Children.Count && i < draft.Terms.Count; i++)
        {
            if (TermsPanel.Children[i] is not StackPanel { Children: [ComboBox fieldBox, ComboBox dirBox, ..] })
            {
                continue;
            }

            string field = (fieldBox.SelectedItem as FieldChoice)?.Field.Key ?? draft.Terms[i].Field;
            bool descending = (dirBox.SelectedItem as DirectionChoice)?.Descending ?? false;
            draft.Terms[i] = new SortTerm(field, descending);
        }
    }

    private string UniqueName()
    {
        const string Stem = "New Sort Order";
        if (_drafts.All(d => d.Name != Stem))
        {
            return Stem;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{Stem} {n}";
            if (_drafts.All(d => d.Name != candidate))
            {
                return candidate;
            }
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Close(false);
            return;
        }

        CaptureTerms();
        if (Validate() is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }

        await _viewModel.ApplySortOrderEditsAsync(_drafts, _deleted);
        Close(true);
    }

    /// <summary>The first thing wrong with the drafts, or null when they are saveable.</summary>
    private string? Validate()
    {
        foreach (SortOrderDraft draft in _drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                return "Every sort order needs a name.";
            }

            if (draft.Terms.Count == 0)
            {
                return $"“{draft.Name}” has no terms; add one or delete the order.";
            }
        }

        // The store's UNIQUE(name) would fail the save halfway through, so
        // collisions are caught here instead.
        string? duplicate = _drafts
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        return duplicate is null ? null : $"More than one sort order is called “{duplicate}”.";
    }
}
