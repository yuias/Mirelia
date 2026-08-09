using Avalonia.Controls;
using Avalonia.Interactivity;
using Mirelia.Query.Ast;
using Mirelia.Query.Builder;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

namespace Mirelia.App.Dialogs;

/// <summary>
/// Edits a smart playlist query as two views of one thing
/// (docs/query-dsl.md): a GUI condition builder and an advanced DSL text
/// editor, kept in sync through <see cref="SimpleQueryModel"/>. Closes
/// with the resulting DSL string, or null on cancel. Queries too complex
/// for the builder stay editable as text.
/// </summary>
public partial class SmartPlaylistEditorDialog : Window
{
    /// <summary>A field option with its kind, for op/value selection.</summary>
    private sealed record FieldChoice(string Name, FieldKind Kind)
    {
        public override string ToString() => Name;
    }

    /// <summary>An operator option with a display label.</summary>
    private sealed record OpChoice(ConditionOp Op, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly OpChoice[] TextOps =
    [
        new(ConditionOp.Contains, "contains"),
        new(ConditionOp.NotContains, "does not contain"),
        new(ConditionOp.Eq, "is"),
        new(ConditionOp.Ne, "is not"),
        new(ConditionOp.StartsWith, "starts with"),
        new(ConditionOp.EndsWith, "ends with"),
        new(ConditionOp.Regex, "matches regex"),
    ];

    private static readonly OpChoice[] ScalarOps =
    [
        new(ConditionOp.Eq, "="),
        new(ConditionOp.Ne, "!="),
        new(ConditionOp.Lt, "<"),
        new(ConditionOp.Le, "<="),
        new(ConditionOp.Gt, ">"),
        new(ConditionOp.Ge, ">="),
    ];

    private static readonly OpChoice[] BoolOps =
    [
        new(ConditionOp.IsTrue, "is true"),
        new(ConditionOp.IsFalse, "is false"),
    ];

    private static readonly FieldChoice[] Fields =
        [.. FieldTable.All.Select(f => new FieldChoice(f.Name, f.Kind)).OrderBy(f => f.Name)];

    private const string OrderNone = "(none)";
    private const string OrderRandom = "random";

    private bool _syncingTabs;
    private int _lastTabIndex;

    /// <summary>XAML-loader constructor.</summary>
    public SmartPlaylistEditorDialog()
        : this(null)
    {
    }

    /// <summary>Creates the editor seeded with existing DSL (null/empty = new).</summary>
    public SmartPlaylistEditorDialog(string? initialDsl)
    {
        InitializeComponent();
        MatchBox.ItemsSource = new[] { "all", "any" };
        MatchBox.SelectedIndex = 0;
        OrderFieldBox.ItemsSource =
            new[] { OrderNone, OrderRandom }.Concat(Fields.Select(f => f.Name)).ToArray();
        OrderFieldBox.SelectedIndex = 0;
        OrderDirBox.ItemsSource = new[] { "Asc", "Desc" };
        OrderDirBox.SelectedIndex = 0;
        OrderDirBox.IsEnabled = false;

        QueryBox.Text = initialDsl ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(initialDsl) && !TryLoadBuilder(initialDsl))
        {
            // Too complex (or invalid) for the builder: open on the text.
            Tabs.SelectedIndex = 1;
        }
        else if (RowsPanel.Children.Count == 0)
        {
            AddRow(null);
        }

        _lastTabIndex = Tabs.SelectedIndex;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        string? dsl = Tabs.SelectedIndex == 0 ? TryBuildDsl() : QueryBox.Text;
        if (dsl is null)
        {
            return; // builder error already shown
        }

        try
        {
            // Full parse/compile validation; cycle checks stay store-side.
            _ = SqlCompiler.Compile(QueryParser.Parse(dsl));
        }
        catch (Exception ex) when (ex is QueryParseException or QueryCompileException)
        {
            ShowError(ex.Message);
            return;
        }

        Close(dsl);
    }

    private void OnAddRowClick(object? sender, RoutedEventArgs e) => AddRow(null);

    private void OnOrderFieldChanged(object? sender, SelectionChangedEventArgs e)
        => OrderDirBox.IsEnabled =
            OrderFieldBox.SelectedItem is string s && s != OrderNone && s != OrderRandom;

    /// <summary>Keeps the two tabs in sync when the user switches views.</summary>
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles up from the row ComboBoxes as well.
        if (!ReferenceEquals(e.Source, Tabs) || _syncingTabs || Tabs.SelectedIndex == _lastTabIndex)
        {
            return;
        }

        _syncingTabs = true;
        try
        {
            HideError();
            if (Tabs.SelectedIndex == 1)
            {
                // Builder → text: render, or stay on the builder with the error.
                if (TryBuildDsl() is { } dsl)
                {
                    QueryBox.Text = dsl;
                }
                else
                {
                    Tabs.SelectedIndex = 0;
                    return;
                }
            }
            else if (!string.IsNullOrWhiteSpace(QueryBox.Text)
                && !TryLoadBuilder(QueryBox.Text))
            {
                Tabs.SelectedIndex = 1;
                return;
            }

            _lastTabIndex = Tabs.SelectedIndex;
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    // ---- builder rows ---------------------------------------------------

    private void AddRow(SimpleCondition? condition)
    {
        var fieldBox = new ComboBox { ItemsSource = Fields, Width = 130 };
        var opBox = new ComboBox { Width = 150 };
        var valueBox = new TextBox();
        var remove = new Button { Content = "✕", Padding = new Avalonia.Thickness(7, 3) };

        fieldBox.SelectionChanged += (_, _) =>
        {
            if (fieldBox.SelectedItem is FieldChoice field)
            {
                OpChoice[] ops = OpsFor(field.Kind);
                ConditionOp? kept = (opBox.SelectedItem as OpChoice)?.Op;
                opBox.ItemsSource = ops;
                opBox.SelectedItem =
                    ops.FirstOrDefault(o => o.Op == kept) ?? ops[0];
                valueBox.IsEnabled = field.Kind != FieldKind.Bool;
                valueBox.PlaceholderText = field.Kind switch
                {
                    FieldKind.Number => "e.g. 80",
                    FieldKind.DateTime => "e.g. 2024-01-01 or @now - 30d",
                    FieldKind.Duration => "e.g. 180s, 10m",
                    _ => null,
                };
            }
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,6,Auto,6,*,6,Auto") };
        Grid.SetColumn(fieldBox, 0);
        Grid.SetColumn(opBox, 2);
        Grid.SetColumn(valueBox, 4);
        Grid.SetColumn(remove, 6);
        row.Children.Add(fieldBox);
        row.Children.Add(opBox);
        row.Children.Add(valueBox);
        row.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            // Keep at least one row so the builder is never empty.
            if (RowsPanel.Children.Count > 1)
            {
                RowsPanel.Children.Remove(row);
            }
        };

        fieldBox.SelectedItem = condition is { } c
            ? Fields.FirstOrDefault(f => f.Name == c.Field) ?? Fields[0]
            : Fields[0];
        if (condition is { } cond)
        {
            opBox.SelectedItem =
                ((OpChoice[])opBox.ItemsSource!).FirstOrDefault(o => o.Op == cond.Op);
            valueBox.Text = cond.ValueText;
        }

        RowsPanel.Children.Add(row);
    }

    private static OpChoice[] OpsFor(FieldKind kind) => kind switch
    {
        FieldKind.Text => TextOps,
        FieldKind.Bool => BoolOps,
        _ => ScalarOps,
    };

    /// <summary>Fills the builder from DSL; false when too complex/invalid.</summary>
    private bool TryLoadBuilder(string dsl)
    {
        ParsedQuery parsed;
        try
        {
            parsed = QueryParser.Parse(dsl);
        }
        catch (QueryParseException)
        {
            return false;
        }

        if (!SimpleQueryModel.TryFrom(parsed, out SimpleQueryModel model))
        {
            return false;
        }

        MatchBox.SelectedIndex = model.MatchAll ? 0 : 1;
        RowsPanel.Children.Clear();
        foreach (SimpleCondition condition in model.Conditions)
        {
            AddRow(condition);
        }

        OrderFieldBox.SelectedItem = model.OrderBy switch
        {
            null => OrderNone,
            { IsRandom: true } => OrderRandom,
            { } term => term.Field,
        };
        OrderDirBox.SelectedIndex = model.OrderBy is { IsRandom: false, Descending: true } ? 1 : 0;
        LimitBox.Text = model.Limit?.ToString() ?? string.Empty;
        return true;
    }

    /// <summary>Renders the builder rows as DSL, or shows an error and returns null.</summary>
    private string? TryBuildDsl()
    {
        var conditions = new List<SimpleCondition>();
        foreach (Control child in RowsPanel.Children)
        {
            if (child is not Grid row)
            {
                continue;
            }

            var fieldBox = (ComboBox)row.Children[0];
            var opBox = (ComboBox)row.Children[1];
            var valueBox = (TextBox)row.Children[2];
            if (fieldBox.SelectedItem is not FieldChoice field
                || opBox.SelectedItem is not OpChoice op)
            {
                ShowError("Every condition needs a field and an operator.");
                return null;
            }

            conditions.Add(new SimpleCondition(field.Name, op.Op, valueBox.Text ?? string.Empty));
        }

        int? limit = null;
        if (!string.IsNullOrWhiteSpace(LimitBox.Text))
        {
            if (!int.TryParse(LimitBox.Text, out int parsed) || parsed <= 0)
            {
                ShowError("Limit must be a positive number.");
                return null;
            }

            limit = parsed;
        }

        OrderTerm? orderBy = OrderFieldBox.SelectedItem switch
        {
            OrderNone or null => null,
            OrderRandom => new OrderTerm(string.Empty, Descending: false, IsRandom: true),
            string field => new OrderTerm(field, OrderDirBox.SelectedIndex == 1),
            _ => null,
        };

        var model = new SimpleQueryModel(
            MatchBox.SelectedIndex == 0, conditions, orderBy, limit);
        try
        {
            string dsl = model.ToDsl();
            // Surface bad value literals ("abc" in a number field) now,
            // pointing at the offending row, not at OK time.
            _ = QueryParser.Parse(dsl);
            HideError();
            return dsl;
        }
        catch (Exception ex) when (ex is InvalidOperationException or QueryParseException)
        {
            ShowError(ex.Message);
            return null;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;
}
