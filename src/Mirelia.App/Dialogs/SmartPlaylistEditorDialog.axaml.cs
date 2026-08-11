using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

    /// <summary>
    /// One group's UI: its own AND/OR toggle and the panel holding its
    /// child rows and nested group borders, in display order.
    /// </summary>
    private sealed class GroupBorder : Border
    {
        public required ComboBox MatchBox { get; init; }

        public required StackPanel ChildrenPanel { get; init; }
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

    private readonly Func<string, Task<int>>? _matchCounter;
    private readonly DispatcherTimer _countDebounce;
    private int _countGeneration;

    private bool _syncingTabs;
    private int _lastTabIndex;
    private GroupBorder _rootGroup = null!;

    /// <summary>XAML-loader constructor.</summary>
    public SmartPlaylistEditorDialog()
        : this(null)
    {
    }

    /// <summary>
    /// Creates the editor seeded with existing DSL (null/empty = new).
    /// <paramref name="matchCounter"/> evaluates a query to its match count
    /// for the live preview; null hides the count.
    /// </summary>
    public SmartPlaylistEditorDialog(string? initialDsl, Func<string, Task<int>>? matchCounter = null)
    {
        InitializeComponent();
        _matchCounter = matchCounter;
        _countDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _countDebounce.Tick += (_, _) =>
        {
            _countDebounce.Stop();
            _ = UpdateMatchCountAsync();
        };
        OrderFieldBox.ItemsSource =
            new[] { OrderNone, OrderRandom }.Concat(Fields.Select(f => f.Name)).ToArray();
        OrderFieldBox.SelectedIndex = 0;
        OrderDirBox.ItemsSource = new[] { "Asc", "Desc" };
        OrderDirBox.SelectedIndex = 0;
        OrderDirBox.IsEnabled = false;

        QueryBox.Text = initialDsl ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(initialDsl) && !TryLoadBuilder(initialDsl))
        {
            // Too complex (or invalid) for the builder: open on the text,
            // but still seed an empty builder so switching tabs works.
            SetRootGroup(new ConditionGroup(true, []));
            Tabs.SelectedIndex = 1;
        }

        if (_rootGroup.ChildrenPanel.Children.Count == 0)
        {
            AddConditionRow(_rootGroup.ChildrenPanel, null);
        }

        _lastTabIndex = Tabs.SelectedIndex;

        LimitBox.TextChanged += (_, _) => ScheduleCount();
        QueryBox.TextChanged += (_, _) => ScheduleCount();
        ScheduleCount();
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

    // ---- builder groups and rows -----------------------------------------

    /// <summary>Replaces the builder's whole tree with the given root group.</summary>
    private void SetRootGroup(ConditionGroup root)
    {
        _rootGroup = BuildGroupUi(root, isRoot: true, parentPanel: null);
        BuilderHost.Children.Clear();
        BuilderHost.Children.Add(_rootGroup);
    }

    /// <summary>
    /// Builds one group's UI recursively: an AND/OR header (with a remove
    /// button for non-root groups), its seeded child rows/subgroups, and
    /// add-condition/add-group buttons.
    /// </summary>
    private GroupBorder BuildGroupUi(ConditionGroup seed, bool isRoot, StackPanel? parentPanel)
    {
        var childrenPanel = new StackPanel { Spacing = 6 };
        var matchBox = new ComboBox
        {
            ItemsSource = new[] { "all", "any" },
            SelectedIndex = seed.MatchAll ? 0 : 1,
            Width = 70,
        };
        matchBox.SelectionChanged += (_, _) => ScheduleCount();

        var group = new GroupBorder
        {
            MatchBox = matchBox,
            ChildrenPanel = childrenPanel,
            BorderBrush = ThemeBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
        };

        var header = new DockPanel();
        if (!isRoot)
        {
            var removeGroup = new Button { Content = "✕ group", Padding = new Thickness(7, 3) };
            DockPanel.SetDock(removeGroup, Dock.Right);
            removeGroup.Click += (_, _) =>
            {
                parentPanel?.Children.Remove(group);
                ScheduleCount();
            };
            header.Children.Add(removeGroup);
        }

        var headerLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        headerLabel.Children.Add(new TextBlock { Text = "Match", VerticalAlignment = VerticalAlignment.Center });
        headerLabel.Children.Add(matchBox);
        headerLabel.Children.Add(new TextBlock
        {
            Text = "of the following:",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        });
        header.Children.Add(headerLabel);

        var addButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var addCondition = new Button { Content = "Add condition", Padding = new Thickness(7, 3) };
        var addGroup = new Button { Content = "Add group", Padding = new Thickness(7, 3) };
        addCondition.Click += (_, _) =>
        {
            AddConditionRow(childrenPanel, null);
            ScheduleCount();
        };
        addGroup.Click += (_, _) =>
        {
            childrenPanel.Children.Add(BuildGroupUi(new ConditionGroup(true, []), isRoot: false, childrenPanel));
            ScheduleCount();
        };
        addButtons.Children.Add(addCondition);
        addButtons.Children.Add(addGroup);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(header);
        content.Children.Add(childrenPanel);
        content.Children.Add(addButtons);
        group.Child = content;

        foreach (ConditionNode node in seed.Children)
        {
            switch (node)
            {
                case ConditionLeaf leaf:
                    AddConditionRow(childrenPanel, leaf.Condition);
                    break;
                case ConditionGroup nested:
                    childrenPanel.Children.Add(BuildGroupUi(nested, isRoot: false, childrenPanel));
                    break;
            }
        }

        return group;
    }

    private void AddConditionRow(StackPanel targetPanel, SimpleCondition? condition)
    {
        var fieldBox = new ComboBox { ItemsSource = Fields, Width = 130 };
        var opBox = new ComboBox { Width = 150 };
        var valueBox = new TextBox();
        var remove = new Button { Content = "✕", Padding = new Thickness(7, 3) };

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
                    FieldKind.DateKey => "e.g. 2025, 2025-08, 2025-08-12",
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
            targetPanel.Children.Remove(row);
            ScheduleCount();
        };
        opBox.SelectionChanged += (_, _) => ScheduleCount();
        valueBox.TextChanged += (_, _) => ScheduleCount();

        fieldBox.SelectedItem = condition is { } c
            ? Fields.FirstOrDefault(f => f.Name == c.Field) ?? Fields[0]
            : Fields[0];
        if (condition is { } cond)
        {
            opBox.SelectedItem =
                ((OpChoice[])opBox.ItemsSource!).FirstOrDefault(o => o.Op == cond.Op);
            valueBox.Text = cond.ValueText;
        }

        targetPanel.Children.Add(row);
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

        SetRootGroup(model.Root);

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

    /// <summary>Renders the builder tree as DSL, or shows an error and returns null.</summary>
    private string? TryBuildDsl()
    {
        string? dsl = BuildDsl(out string? error);
        if (dsl is null)
        {
            ShowError(error ?? "Invalid query.");
        }
        else
        {
            HideError();
        }

        return dsl;
    }

    /// <summary>Renders the builder tree as DSL without touching the error UI.</summary>
    private string? BuildDsl(out string? error)
    {
        error = null;
        if (!TryReadGroup(_rootGroup, out ConditionGroup root, out error))
        {
            return null;
        }

        int? limit = null;
        if (!string.IsNullOrWhiteSpace(LimitBox.Text))
        {
            if (!int.TryParse(LimitBox.Text, out int parsed) || parsed <= 0)
            {
                error = "Limit must be a positive number.";
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

        var model = new SimpleQueryModel(root, orderBy, limit);
        try
        {
            string dsl = model.ToDsl();
            // Surface bad value literals ("abc" in a number field) now,
            // pointing at the offending row, not at OK time.
            _ = QueryParser.Parse(dsl);
            return dsl;
        }
        catch (Exception ex) when (ex is InvalidOperationException or QueryParseException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Reads one group's UI state back into a <see cref="ConditionGroup"/>, recursively.</summary>
    private static bool TryReadGroup(GroupBorder groupBorder, out ConditionGroup group, out string? error)
    {
        error = null;
        bool matchAll = groupBorder.MatchBox.SelectedIndex == 0;
        var children = new List<ConditionNode>();
        foreach (Control child in groupBorder.ChildrenPanel.Children)
        {
            switch (child)
            {
                case Grid row:
                    var fieldBox = (ComboBox)row.Children[0];
                    var opBox = (ComboBox)row.Children[1];
                    var valueBox = (TextBox)row.Children[2];
                    if (fieldBox.SelectedItem is not FieldChoice field || opBox.SelectedItem is not OpChoice op)
                    {
                        error = "Every condition needs a field and an operator.";
                        group = null!;
                        return false;
                    }

                    children.Add(new ConditionLeaf(
                        new SimpleCondition(field.Name, op.Op, valueBox.Text ?? string.Empty)));
                    break;

                case GroupBorder nested:
                    if (!TryReadGroup(nested, out ConditionGroup subGroup, out error))
                    {
                        group = null!;
                        return false;
                    }

                    children.Add(subGroup);
                    break;
            }
        }

        group = new ConditionGroup(matchAll, children);
        return true;
    }

    // ---- live match count (kickoff M6) ----------------------------------

    private void ScheduleCount()
    {
        if (_matchCounter is not null)
        {
            _countDebounce.Stop();
            _countDebounce.Start();
        }
    }

    private async Task UpdateMatchCountAsync()
    {
        if (_matchCounter is null)
        {
            return;
        }

        string? dsl = Tabs.SelectedIndex == 0
            ? BuildDsl(out _)
            : string.IsNullOrWhiteSpace(QueryBox.Text) ? null : QueryBox.Text;
        int generation = ++_countGeneration;
        if (dsl is null)
        {
            MatchCountText.Text = string.Empty;
            return;
        }

        try
        {
            int count = await _matchCounter(dsl);
            if (generation == _countGeneration)
            {
                MatchCountText.Text = $"{count:N0} matching track(s)";
            }
        }
        catch (Exception ex) when (ex is QueryParseException or QueryCompileException)
        {
            // Mid-edit queries fail routinely; just blank the count.
            if (generation == _countGeneration)
            {
                MatchCountText.Text = string.Empty;
            }
        }
    }

    private static IBrush ThemeBorderBrush { get; } =
        Application.Current?.TryFindResource("SystemControlForegroundBaseLowBrush", out object? res) == true
        && res is IBrush brush
            ? brush
            : Brushes.Gray;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;
}
