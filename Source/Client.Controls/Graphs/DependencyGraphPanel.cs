using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Heron.ClientControls.Graphs;

/// <summary>Null dependencies preserve the ordering of legacy linear lists; empty means an entry.</summary>
public interface IGraphItem
{
    string Key { get; }
    IReadOnlyList<string>? DependsOn { get; }
    /// <summary>Conditional transfers, drawn separately from normal dependency edges.</summary>
    IReadOnlyList<string> ConditionalSources => [];
}

/// <summary>
/// Content-agnostic layered DAG layout, with fork/join connectors behind item templates.
/// Steps share a content-fitted width; horizontal lanes can fit their individual nested content widths.
/// </summary>
public sealed class DependencyGraphPanel : Panel
{
    public static readonly StyledProperty<double> NodeWidthProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, double>(nameof(NodeWidth), 180);
    public static readonly StyledProperty<double> MaxNodeWidthProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, double>(nameof(MaxNodeWidth), 360);
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, Orientation>(nameof(Orientation), Orientation.Vertical);
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public static readonly StyledProperty<bool> StretchNodesProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, bool>(nameof(StretchNodes));
    /// <summary>Fill the lane width when space is available; wide parallel branches can still scroll.</summary>
    public bool StretchNodes { get => GetValue(StretchNodesProperty); set => SetValue(StretchNodesProperty, value); }
    public static readonly StyledProperty<bool> FitNodeWidthsProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, bool>(nameof(FitNodeWidths));
    /// <summary>Horizontal lanes use individual content widths, allowing nested branches to expand beyond MaxNodeWidth.</summary>
    public bool FitNodeWidths { get => GetValue(FitNodeWidthsProperty); set => SetValue(FitNodeWidthsProperty, value); }
    public static readonly StyledProperty<bool> StackBranchesProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, bool>(nameof(StackBranches));
    /// <summary>Stack complete sequential branch blocks vertically, with cross-branch connections at the sides.</summary>
    public bool StackBranches { get => GetValue(StackBranchesProperty); set => SetValue(StackBranchesProperty, value); }

    /// <summary>Minimum node width.</summary>
    public double NodeWidth { get => GetValue(NodeWidthProperty); set => SetValue(NodeWidthProperty, value); }
    /// <summary>Upper bound for the content-fitted width; longer text wraps.</summary>
    public double MaxNodeWidth { get => GetValue(MaxNodeWidthProperty); set => SetValue(MaxNodeWidthProperty, value); }
    public static readonly StyledProperty<double> NodeSpacingProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, double>(nameof(NodeSpacing), 24);
    /// <summary>Space between nodes and connection lanes.</summary>
    public double NodeSpacing { get => GetValue(NodeSpacingProperty); set => SetValue(NodeSpacingProperty, value); }
    public static readonly StyledProperty<IBrush?> ConnectorBrushProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, IBrush?>(nameof(ConnectorBrush));
    /// <summary>Dependency edge color; null keeps the legacy neutral blue.</summary>
    public IBrush? ConnectorBrush { get => GetValue(ConnectorBrushProperty); set => SetValue(ConnectorBrushProperty, value); }
    public static readonly StyledProperty<double> ConnectorThicknessProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, double>(nameof(ConnectorThickness), 1.5);
    public double ConnectorThickness { get => GetValue(ConnectorThicknessProperty); set => SetValue(ConnectorThicknessProperty, value); }
    public static readonly StyledProperty<IBrush?> ConditionalBrushProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, IBrush?>(nameof(ConditionalBrush));
    /// <summary>Conditional transfer (finally/recovery) lane color; null keeps the legacy amber.</summary>
    public IBrush? ConditionalBrush { get => GetValue(ConditionalBrushProperty); set => SetValue(ConditionalBrushProperty, value); }
    public static readonly StyledProperty<IBrush?> BranchBackgroundProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, IBrush?>(nameof(BranchBackground));
    /// <summary>Fill behind each stacked parallel branch; null keeps the legacy translucent gray.</summary>
    public IBrush? BranchBackground { get => GetValue(BranchBackgroundProperty); set => SetValue(BranchBackgroundProperty, value); }
    public static readonly StyledProperty<IBrush?> BranchBorderBrushProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, IBrush?>(nameof(BranchBorderBrush));
    /// <summary>Outline of each stacked parallel branch; null keeps the legacy outline, Transparent hides it.</summary>
    public IBrush? BranchBorderBrush { get => GetValue(BranchBorderBrushProperty); set => SetValue(BranchBorderBrushProperty, value); }
    public static readonly StyledProperty<double> BranchCornerRadiusProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, double>(nameof(BranchCornerRadius), 6);
    public double BranchCornerRadius { get => GetValue(BranchCornerRadiusProperty); set => SetValue(BranchCornerRadiusProperty, value); }
    public static readonly StyledProperty<double> ConnectorAnchorYProperty =
        AvaloniaProperty.Register<DependencyGraphPanel, double>(nameof(ConnectorAnchorY), 28);
    /// <summary>Horizontal lanes: edge height measured from a node's top, normally the header center.</summary>
    public double ConnectorAnchorY { get => GetValue(ConnectorAnchorYProperty); set => SetValue(ConnectorAnchorYProperty, value); }
    private double Gap => NodeSpacing;
    private readonly List<(Control Child, string Key, string[] Parents, string[] ConditionalSources, int Rank)> _nodes = [];
    private readonly Dictionary<string, Rect> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _nodeWidths = new(StringComparer.Ordinal);
    private readonly List<string[]> _branches = [];
    private readonly Dictionary<string, int> _branchOwners = new(StringComparer.Ordinal);
    private readonly List<Rect> _branchBounds = [];
    private double _cellWidth;
    private double _connectionLane;
    private bool _horizontal;

    static DependencyGraphPanel() => AffectsMeasure<DependencyGraphPanel>(NodeWidthProperty, MaxNodeWidthProperty,
        OrientationProperty, StretchNodesProperty, FitNodeWidthsProperty, StackBranchesProperty, NodeSpacingProperty,
        ConnectorBrushProperty, ConnectorThicknessProperty, ConditionalBrushProperty, BranchBackgroundProperty,
        BranchBorderBrushProperty, BranchCornerRadiusProperty, ConnectorAnchorYProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        _nodes.Clear();
        _nodeWidths.Clear();
        var items = Children.Select((child, index) =>
        {
            var item = (child as ContentPresenter)?.Content as IGraphItem ?? child.DataContext as IGraphItem;
            return (Child: child, Key: string.IsNullOrEmpty(item?.Key) ? index.ToString() : item.Key,
                Parents: item?.DependsOn, ConditionalSources: item?.ConditionalSources.ToArray() ?? []);
        }).ToArray();
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var byKey = items.Select((item, index) => (item.Key, index)).ToDictionary(p => p.Key, p => p.index);
        string[] Parents(int index) => items[index].Parents?.ToArray()
            ?? (index == 0 ? [] : [items[index - 1].Key]);
        int Rank(int index, HashSet<string> visiting)
        {
            var key = items[index].Key;
            if (ranks.TryGetValue(key, out var rank)) return rank;
            if (!visiting.Add(key)) return 0; // Malformed historical data must not recurse indefinitely.
            rank = Parents(index).Concat(items[index].ConditionalSources).Where(byKey.ContainsKey)
                .Select(parent => Rank(byKey[parent], visiting) + 1).DefaultIfEmpty(0).Max();
            visiting.Remove(key);
            return ranks[key] = rank;
        }
        for (var i = 0; i < items.Length; i++)
            _nodes.Add((items[i].Child, items[i].Key, Parents(i), items[i].ConditionalSources, Rank(i, [])));
        _horizontal = Orientation == Orientation.Horizontal;
        BuildBranches();
        var columns = StackBranches ? 1 : _nodes.GroupBy(n => n.Rank).Select(g => g.Count()).DefaultIfEmpty(1).Max();
        var maxNode = Math.Max(NodeWidth, MaxNodeWidth);
        // Fit the widest node, but never wider than the cap nor than what the viewport can hold per column.
        if (!_horizontal && double.IsFinite(availableSize.Width))
            maxNode = Math.Max(NodeWidth, Math.Min(maxNode, (availableSize.Width - (columns - 1) * Gap - BranchInsets) / columns));
        var natural = 0d;
        foreach (var node in _nodes)
        {
            node.Child.Measure(new Size(_horizontal && FitNodeWidths ? double.PositiveInfinity : maxNode, double.PositiveInfinity));
            natural = Math.Max(natural, node.Child.DesiredSize.Width);
            _nodeWidths[node.Key] = Math.Max(NodeWidth, node.Child.DesiredSize.Width);
        }
        _cellWidth = Math.Ceiling(Math.Clamp(natural, NodeWidth, maxNode));
        foreach (var node in _nodes)
            node.Child.Measure(new Size(_horizontal && FitNodeWidths ? _nodeWidths[node.Key] : _cellWidth, double.PositiveInfinity));
        _connectionLane = _nodes.Any(node => node.ConditionalSources.Length > 0) ? Gap : 0;
        if (_horizontal) return LayoutHorizontal(arrange: false);
        if (StackBranches) return LayoutBranches(_cellWidth + BranchInsets, arrange: false);
        var width = columns * _cellWidth + (columns - 1) * Gap;
        var rows = _nodes.GroupBy(n => n.Rank).ToArray();
        return new Size(width, rows.Sum(row => row.Max(n => n.Child.DesiredSize.Height)) + Math.Max(0, rows.Length - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _positions.Clear();
        if (_horizontal)
        {
            LayoutHorizontal(arrange: true);
            DrawConnections(finalSize);
            return finalSize;
        }
        if (StackBranches)
        {
            LayoutBranches(finalSize.Width, arrange: true);
            DrawConnections(finalSize);
            return finalSize;
        }
        var columns = _nodes.GroupBy(node => node.Rank).Select(layer => layer.Count()).DefaultIfEmpty(1).Max();
        var width = StretchNodes ? Math.Max(_cellWidth, (finalSize.Width - (columns - 1) * Gap) / columns) : _cellWidth;
        double y = 0;
        foreach (var row in _nodes.GroupBy(n => n.Rank).OrderBy(g => g.Key))
        {
            double ParentCenter(string[] parents) => parents.Where(_positions.ContainsKey)
                .Select(parent => _positions[parent].Center.X).DefaultIfEmpty(finalSize.Width / 2).Average();
            var nodes = row.OrderBy(node => ParentCenter(node.Parents)).ToArray();
            // Fit ordered sibling blocks around their parents without overlaps. A lone
            // successor stays in its branch column, even when other branches have ended.
            var blocks = new List<(int Start, int Count, double Offset)>();
            for (var index = 0; index < nodes.Length; index++)
            {
                blocks.Add((index, 1, ParentCenter(nodes[index].Parents) - width / 2 - index * (width + Gap)));
                while (blocks.Count > 1 && blocks[^2].Offset > blocks[^1].Offset)
                {
                    var right = blocks[^1];
                    var left = blocks[^2];
                    blocks.RemoveRange(blocks.Count - 2, 2);
                    blocks.Add((left.Start, left.Count + right.Count,
                        (left.Offset * left.Count + right.Offset * right.Count) / (left.Count + right.Count)));
                }
            }
            var height = nodes.Max(n => n.Child.DesiredSize.Height);
            var maxOffset = Math.Max(0, finalSize.Width - nodes.Length * width - (nodes.Length - 1) * Gap);
            foreach (var block in blocks)
            for (var index = block.Start; index < block.Start + block.Count; index++)
            {
                var node = nodes[index];
                var x = Math.Clamp(block.Offset, 0, maxOffset) + index * (width + Gap);
                var rect = new Rect(x, y, width, node.Child.DesiredSize.Height);
                node.Child.Arrange(rect);
                _positions[node.Key] = rect;
            }
            y += height + Gap;
        }
        DrawConnections(finalSize);
        return finalSize;
    }

    private double BranchInsets => StackBranches ? (_branches.Count > 1 ? Gap * 3 : Gap) : 0;

    private void BuildBranches()
    {
        _branches.Clear();
        _branchOwners.Clear();
        if (!StackBranches || _horizontal) return;
        var known = _nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        var parents = _nodes.ToDictionary(node => node.Key, node => node.Parents.Where(known.Contains).ToArray());
        var next = _nodes.ToDictionary(node => node.Key, node => _nodes.Where(child => child.Parents.Contains(node.Key)).Select(child => child.Key).ToArray());
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        void AddBranch(string head)
        {
            var branch = new List<string>();
            var key = head;
            while (assigned.Add(key))
            {
                branch.Add(key);
                _branchOwners[key] = _branches.Count;
                if (next[key].Length != 1 || parents[next[key][0]].Length != 1) break;
                key = next[key][0];
            }
            if (branch.Count > 0) _branches.Add(branch.ToArray());
        }
        foreach (var node in _nodes.OrderBy(node => node.Rank))
            if (parents[node.Key].Length != 1 || next[parents[node.Key][0]].Length != 1) AddBranch(node.Key);
        // Historical graphs may contain cycles; retain every visible node without recursive traversal.
        foreach (var node in _nodes) if (!assigned.Contains(node.Key)) AddBranch(node.Key);
    }

    private Size LayoutBranches(double availableWidth, bool arrange)
    {
        _positions.Clear();
        _branchBounds.Clear();
        var inset = BranchInsets / 2;
        var width = StretchNodes ? Math.Max(_cellWidth, availableWidth - BranchInsets) : _cellWidth;
        var byKey = _nodes.ToDictionary(node => node.Key);
        double y = 0;
        foreach (var branch in _branches)
        {
            var top = y;
            if (_branches.Count > 1) y += Gap / 2;
            foreach (var key in branch)
            {
                var node = byKey[key];
                var rect = new Rect(inset, y, width, node.Child.DesiredSize.Height);
                if (arrange) node.Child.Arrange(rect);
                _positions[key] = rect;
                y = rect.Bottom + Gap;
            }
            y -= Gap;
            if (_branches.Count > 1) y += Gap / 2;
            _branchBounds.Add(new Rect(Gap, top, width + Gap, y - top));
            y += Gap;
        }
        return new Size(width + BranchInsets, Math.Max(0, y - Gap));
    }

    // Card-sized nodes (a single step) anchor at their own vertical center; taller stage cards anchor at the header row.
    private double AnchorY(Control child, Rect rect) =>
        rect.Height < ConnectorAnchorY * 3 ? rect.Center.Y : rect.Top + ConnectorAnchorY;

    private double[] ConnectionHeights(Control child, bool incoming)
    {
        var panel = child.GetVisualDescendants().OfType<DependencyGraphPanel>()
            .FirstOrDefault(candidate => candidate.StackBranches && candidate._branches.Count > 1);
        if (panel == null) return [];
        var keys = panel._positions.Keys.ToHashSet(StringComparer.Ordinal);
        var anchors = panel._nodes.Where(node => incoming
            ? !node.Parents.Any(keys.Contains)
            : !panel._nodes.Any(next => next.Parents.Contains(node.Key))).ToArray();
        if (anchors.Length < 2) return [];
        return anchors.Select(node => panel.TranslatePoint(new Point(0, panel._positions[node.Key].Center.Y), this)?.Y)
            .OfType<double>().ToArray();
    }

    private Size LayoutHorizontal(bool arrange)
    {
        _positions.Clear();
        double x = 0, bottom = 0;
        foreach (var layer in _nodes.GroupBy(node => node.Rank).OrderBy(layer => layer.Key))
        {
            double ParentTop(string[] parents) => parents.Where(_positions.ContainsKey)
                .Select(parent => _positions[parent].Top).DefaultIfEmpty(_connectionLane).Average();
            double top = _connectionLane;
            double layerWidth = 0;
            foreach (var node in layer.OrderBy(node => ParentTop(node.Parents)))
            {
                // Unequal branch lengths keep their vertical lane until the actual join.
                top = Math.Max(top, ParentTop(node.Parents));
                var width = FitNodeWidths ? _nodeWidths[node.Key] : _cellWidth;
                var rect = new Rect(x, top, width, node.Child.DesiredSize.Height);
                layerWidth = Math.Max(layerWidth, width);
                if (arrange) node.Child.Arrange(rect);
                _positions[node.Key] = rect;
                bottom = Math.Max(bottom, rect.Bottom);
                top = rect.Bottom + Gap;
            }
            x += layerWidth + Gap;
        }
        return new Size(Math.Max(0, x - Gap), bottom);
    }

    private void DrawConnections(Size size)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(size));
            var pen = new Pen(ConnectorBrush ?? Brush.Parse("#B8C8D8"), ConnectorThickness);
            if (!_horizontal && StackBranches && _branches.Count > 1)
            {
                var branchFill = BranchBackground ?? Brush.Parse("#06808080");
                var branchPen = new Pen(BranchBorderBrush ?? Brush.Parse("#B8C8D8"), 1);
                foreach (var bounds in _branchBounds)
                    context.DrawRectangle(branchFill, branchPen, bounds, BranchCornerRadius, BranchCornerRadius);
                var entries = _nodes.Where(node => !node.Parents.Any(_positions.ContainsKey)).ToArray();
                var exits = _nodes.Where(node => !_nodes.Any(next => next.Parents.Contains(node.Key))).ToArray();
                if (entries.Length > 1)
                    foreach (var node in entries)
                    {
                        var rect = _positions[node.Key];
                        var end = new Point(rect.Left, rect.Center.Y);
                        context.DrawLine(pen, new Point(0, end.Y), end);
                        context.DrawLine(pen, end, new Point(end.X - 4, end.Y - 3));
                        context.DrawLine(pen, end, new Point(end.X - 4, end.Y + 3));
                    }
                if (exits.Length > 1)
                    foreach (var node in exits)
                    {
                        var rect = _positions[node.Key];
                        context.DrawLine(pen, new Point(rect.Right, rect.Center.Y), new Point(size.Width, rect.Center.Y));
                    }
            }
            foreach (var node in _nodes)
            foreach (var parent in node.Parents)
            {
                if (!_positions.TryGetValue(parent, out var from) || !_positions.TryGetValue(node.Key, out var to)) continue;
                if (_horizontal)
                {
                    var sourceYs = ConnectionHeights(_nodes.First(item => item.Key == parent).Child, false);
                    var targetYs = ConnectionHeights(node.Child, true);
                    var entersBranches = targetYs.Length > 0;
                    if (sourceYs.Length == 0) sourceYs = [AnchorY(_nodes.First(item => item.Key == parent).Child, from)];
                    if (targetYs.Length == 0) targetYs = [AnchorY(node.Child, to)];
                    var middle = to.Left - Gap / 2;
                    foreach (var sourceY in sourceYs)
                        context.DrawLine(pen, new Point(from.Right, sourceY), new Point(middle, sourceY));
                    var heights = sourceYs.Concat(targetYs).ToArray();
                    context.DrawLine(pen, new Point(middle, heights.Min()), new Point(middle, heights.Max()));
                    foreach (var targetY in targetYs)
                    {
                        var target = new Point(to.Left, targetY);
                        context.DrawLine(pen, new Point(middle, targetY), target);
                        if (!entersBranches)
                        {
                            context.DrawLine(pen, target, new Point(target.X - 4, target.Y - 3));
                            context.DrawLine(pen, target, new Point(target.X - 4, target.Y + 3));
                        }
                    }
                    continue;
                }
                if (StackBranches && _branchOwners[parent] != _branchOwners[node.Key])
                {
                    var source = new Point(from.Left, from.Center.Y);
                    var target = new Point(to.Left, to.Center.Y);
                    var side = Gap / 2;
                    context.DrawLine(pen, source, new Point(side, source.Y));
                    context.DrawLine(pen, new Point(side, source.Y), new Point(side, target.Y));
                    context.DrawLine(pen, new Point(side, target.Y), target);
                    context.DrawLine(pen, target, new Point(target.X - 4, target.Y - 3));
                    context.DrawLine(pen, target, new Point(target.X - 4, target.Y + 3));
                    continue;
                }
                var start = new Point(from.Center.X, from.Bottom);
                var end = new Point(to.Center.X, to.Top);
                // Join below the last branch step, rather than crossing intermediate cards.
                var midY = end.Y - Gap / 2;
                context.DrawLine(pen, start, new Point(start.X, midY));
                context.DrawLine(pen, new Point(start.X, midY), new Point(end.X, midY));
                context.DrawLine(pen, new Point(end.X, midY), end);
                context.DrawLine(pen, end, new Point(end.X - 3, end.Y - 4));
                context.DrawLine(pen, end, new Point(end.X + 3, end.Y - 4));
            }
            // A separate overhead lane expresses a conditional transfer from any phase.
            // It must not look like a join that waits for all normal dependencies.
            if (_horizontal)
            {
                var conditional = new Pen(ConditionalBrush ?? Brush.Parse("#B27A2E"), ConnectorThickness, new DashStyle([4d, 3d], 0));
                foreach (var node in _nodes.Where(node => node.ConditionalSources.Length > 0))
                {
                    if (!_positions.TryGetValue(node.Key, out var to)) continue;
                    var end = new Point(to.Center.X, to.Top);
                    var sources = node.ConditionalSources.Where(_positions.ContainsKey).Select(key => _positions[key]).ToArray();
                    if (sources.Length == 0) continue;
                    var laneY = Gap / 4;
                    context.DrawLine(conditional, new Point(sources.Min(source => source.Right) + Gap / 2, laneY), new Point(end.X, laneY));
                    foreach (var from in sources)
                    {
                        var start = new Point(from.Right, from.Top + ConnectorAnchorY);
                        var turn = new Point(from.Right + Gap / 2, start.Y);
                        context.DrawLine(conditional, start, turn);
                        context.DrawLine(conditional, turn, new Point(turn.X, laneY));
                    }
                    context.DrawLine(conditional, new Point(end.X, laneY), end);
                    context.DrawLine(pen, end, new Point(end.X - 3, end.Y - 4));
                    context.DrawLine(pen, end, new Point(end.X + 3, end.Y - 4));
                }
            }
        }
        Background = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
    }
}
