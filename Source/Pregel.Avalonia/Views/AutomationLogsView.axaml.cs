using System;
using System.Collections.Generic;
using Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using Heron.Pregel.Avalonia.ViewModels;

namespace Heron.Pregel.Avalonia.Views;

public partial class AutomationLogsView : UserControl
{
    private AutomationLogsViewModel? _model;
    private bool _attached;
    private bool _restoring;
    private double _offset;
    private double _lastOffset;
    private double? _pagingAnchor;
    private int _update;
    private readonly List<Visual> _ancestors = [];
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string _searchText = "";
    private string _query = "";
    private int _matchCount;
    private int _matchIndex;
    private int _matchOffset = -1;
    private bool _navigateAfterSearch;

    public AutomationLogsView()
    {
        InitializeComponent();
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            var navigate = _navigateAfterSearch;
            _navigateAfterSearch = false;
            RefreshSearch(navigate);
        };
        LogEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            var direction = Math.Sign(LogEditor.VerticalOffset - _lastOffset);
            _lastOffset = LogEditor.VerticalOffset;
            if (!_restoring) CheckScroll(direction);
        };
        LogEditor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.Y > 0) PauseFollowing();
            var direction = -Math.Sign(e.Delta.Y);
            Dispatcher.UIThread.Post(() => CheckScroll(direction), DispatcherPriority.Loaded);
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnLogKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model != null)
        {
            _model.Updating -= BeforeUpdate;
            _model.Updated -= AfterUpdate;
            _model.SetActive(false);
        }
        ++_update;
        _pagingAnchor = null;
        _restoring = false;
        _model = DataContext as AutomationLogsViewModel;
        if (_model != null)
        {
            _model.Updating += BeforeUpdate;
            _model.Updated += AfterUpdate;
        }
        base.OnDataContextChanged(e);
        UpdateActive();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _ancestors.AddRange(this.GetVisualAncestors());
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged += AncestorChanged;
        UpdateActive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= AncestorChanged;
        _ancestors.Clear();
        _searchDelay.Stop();
        ++_update;
        _restoring = false;
        _model?.SetActive(false);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Dispatcher.UIThread.Post(UpdateActive);
    }

    private void AncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) Dispatcher.UIThread.Post(UpdateActive);
    }

    private void UpdateActive() => _model?.SetActive(_attached && IsEffectivelyVisible);
    private void PauseFollowing()
    {
        if (_model != null) _model.FollowLatest = false;
    }

    private bool AtBottom => LogEditor.VerticalOffset + LogEditor.ViewportHeight >= LogEditor.ExtentHeight - 4;

    private void CheckScroll(int direction)
    {
        if (_restoring || !_attached || !IsEffectivelyVisible || _model == null) return;
        BackToLatest.IsVisible = !AtBottom || _model.CanLoadNewer;
        if (direction == 0 || _model.IsLoading) return;
        if (direction < 0) PauseFollowing();
        if ((direction < 0 && LogEditor.VerticalOffset <= 4 && _model.CanLoadEarlier)
            || (direction > 0 && AtBottom && _model.CanLoadNewer))
        {
            _pagingAnchor = _model.FirstLine + LogEditor.VerticalOffset / Math.Max(1, LogEditor.TextArea.TextView.DefaultLineHeight);
            if (direction < 0) _model.LoadEarlierCommand.Execute(null);
            else _model.LoadNewerCommand.Execute(null);
        }
        else if (direction > 0 && AtBottom && !_model.FollowLatest
                 && string.IsNullOrEmpty(SearchBox.Text) && LogEditor.SelectionLength == 0)
            _model.FollowLatest = true;
    }

    private void BeforeUpdate()
    {
        _restoring = true;
        _offset = LogEditor.VerticalOffset;
    }

    private void AfterUpdate(bool reset, int? focusLine)
    {
        var update = ++_update;
        var model = _model;
        var offset = _offset;
        var anchor = _pagingAnchor;
        _pagingAnchor = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (update != _update || !ReferenceEquals(model, _model) || !_attached) return;
            if (model?.FollowLatest == true) LogEditor.ScrollToEnd();
            else if (anchor is { } row && model != null)
                LogEditor.ScrollToVerticalOffset(Math.Max(0, row - model.FirstLine) * LogEditor.TextArea.TextView.DefaultLineHeight);
            else if (focusLine is { } line) LogEditor.ScrollToLine(line);
            else if (reset) LogEditor.ScrollToHome();
            else LogEditor.ScrollToVerticalOffset(offset);
            _lastOffset = LogEditor.VerticalOffset;
            _restoring = false;
            CheckScroll(0);
            if (!string.IsNullOrEmpty(SearchBox.Text)) QueueSearch(false);
        }, DispatcherPriority.Loaded);
    }

    private void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            PauseFollowing();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true; // Keep the editor's floating search panel closed.
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Text = "";
            LogEditor.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            MoveMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (!SearchBox.IsKeyboardFocusWithin && e.Key is Key.Up or Key.PageUp or Key.Home or Key.Down or Key.PageDown or Key.End)
        {
            var direction = e.Key is Key.Up or Key.PageUp or Key.Home ? -1 : 1;
            if (direction < 0) PauseFollowing();
            Dispatcher.UIThread.Post(() => CheckScroll(direction), DispatcherPriority.Loaded);
        }
    }

    private void Search_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_attached) return;
        if (!string.IsNullOrEmpty(SearchBox.Text)) PauseFollowing();
        QueueSearch();
    }

    private void QueueSearch(bool navigate = true)
    {
        _navigateAfterSearch |= navigate;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void RefreshSearch(bool navigate = true)
    {
        _query = SearchBox.Text ?? "";
        _searchText = _query.Length == 0 ? "" : LogEditor.Document.Text;
        _matchCount = 0;
        _matchIndex = 0;
        _matchOffset = -1;
        if (_query.Length > 0)
        {
            for (var start = 0; start <= _searchText.Length - _query.Length;)
            {
                var found = _searchText.IndexOf(_query, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                if (_matchOffset < 0) _matchOffset = found;
                ++_matchCount;
                start = found + 1;
            }
            if (_matchCount > 0 && navigate) _matchIndex = 1;
            else _matchOffset = -1;
        }
        ShowMatch();
    }

    private void MoveMatch(int direction)
    {
        if (_searchDelay.IsEnabled)
        {
            _searchDelay.Stop();
            _navigateAfterSearch = false;
            RefreshSearch();
            return;
        }
        if (_matchCount == 0) return;
        if (direction > 0)
        {
            _matchOffset = _searchText.IndexOf(_query, _matchOffset + 1, StringComparison.OrdinalIgnoreCase);
            _matchIndex++;
            if (_matchOffset < 0) { _matchOffset = _searchText.IndexOf(_query, StringComparison.OrdinalIgnoreCase); _matchIndex = 1; }
        }
        else
        {
            _matchOffset = _matchOffset <= 0 ? -1 : _searchText.LastIndexOf(_query,
                Math.Min(_searchText.Length - 1, _matchOffset + _query.Length - 2), StringComparison.OrdinalIgnoreCase);
            _matchIndex--;
            if (_matchOffset < 0) { _matchOffset = _searchText.LastIndexOf(_query, StringComparison.OrdinalIgnoreCase); _matchIndex = _matchCount; }
        }
        ShowMatch();
    }

    private void ShowMatch()
    {
        SearchCount.Text = _query.Length == 0 ? "" : _matchCount == 0 ? "无匹配"
            : _matchIndex == 0 ? $"{_matchCount} 处" : $"{_matchIndex}/{_matchCount}";
        PreviousMatch.IsEnabled = NextMatch.IsEnabled = _matchCount > 0;
        if (_matchOffset < 0) { LogEditor.SelectionLength = 0; return; }
        _restoring = true;
        LogEditor.Select(_matchOffset, _query.Length);
        LogEditor.ScrollToLine(LogEditor.Document.GetLineByOffset(_matchOffset).LineNumber);
        _lastOffset = LogEditor.VerticalOffset;
        _restoring = false;
        CheckScroll(0);
    }

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        MoveMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        e.Handled = true;
    }
    private void PreviousMatch_Click(object? sender, RoutedEventArgs e) => MoveMatch(-1);
    private void NextMatch_Click(object? sender, RoutedEventArgs e) => MoveMatch(1);
    private void BackToLatest_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _searchDelay.Stop();
        _navigateAfterSearch = false;
        RefreshSearch();
        _pagingAnchor = null;
        _model?.LatestCommand.Execute(null);
    }

    internal bool ClearSearch()
    {
        if (string.IsNullOrEmpty(SearchBox.Text)) return false;
        SearchBox.Text = "";
        LogEditor.Focus();
        return true;
    }
}
