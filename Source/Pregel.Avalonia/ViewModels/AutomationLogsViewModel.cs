using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationLogsViewModel : ViewModelBase, IDisposable
{
    private readonly Func<Task> _export;
    public AutomationLogsViewModel(Func<Task> export)
    {
        _export = export;
        Document.UndoStack.SizeLimit = 0;
    }
    private AutomationRunRecord? _record;
    private CancellationTokenSource? _reading;
    private bool _active;
    private bool _caughtUp;
    private bool _changing;
    private long? _endLine;
    private readonly Stack<long?> _newerPages = new();
    private long _firstLine;
    private long _lastLine;
    private long _total;
    private int _generation;

    public TextDocument Document { get; } = new();
    public ObservableCollection<AutomationLogScope> Scopes { get; } = [];
    [ObservableProperty] private AutomationLogScope? _selectedScope;
    [ObservableProperty] private bool _followLatest;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _notice = "";
    public string RangeText => _lastLine == 0 ? (IsLoading ? "正在扫描日志…" : "暂无日志")
        : IsLoading ? "正在加载…"
        : _firstLine == 1 && _lastLine == _total ? $"{_total:N0} 行"
        : $"{_firstLine:N0}–{_lastLine:N0} / {_total:N0} 行";
    internal long FirstLine => _firstLine;
    public bool CanLoadEarlier => !IsLoading && _firstLine > 1;
    public bool CanLoadNewer => !IsLoading && (_newerPages.Count > 0 || _lastLine < _total);
    public event Action? Updating;
    public event Action<bool, int?>? Updated;

    internal void SetRun(AutomationRunRecord? record)
    {
        if (ReferenceEquals(_record, record)) return;
        if (_record?.RunId == record?.RunId)
        {
            _record = record;
            if (record != null) SynchronizeScopes(record.Steps);
            return;
        }
        Stop();
        _record = record;
        _changing = true;
        Scopes.Clear();
        Scopes.Add(new("", "全部日志", []));
        if (record != null) AddScopes(Scopes, record.Steps, "", "", []);
        SelectedScope = Scopes[0];
        FollowLatest = record != null && !record.IsFinished;
        _endLine = null;
        _newerPages.Clear();
        _changing = false;
        Clear();
        Restart();
    }

    private static void AddScopes(ICollection<AutomationLogScope> scopes, IEnumerable<AutomationStepRecord> steps, string keyPrefix, string titlePrefix,
        IReadOnlyList<HashSet<string>> parentPath)
    {
        foreach (var step in steps)
        {
            var key = keyPrefix + "/" + step.Key;
            var title = titlePrefix + step.Title;
            var path = parentPath.Append(step.NodeIds.ToHashSet(StringComparer.Ordinal)).ToArray();
            scopes.Add(new(key, title, path));
            AddScopes(scopes, step.Children, key, title + " / ", step.IsStage ? parentPath : path);
        }
    }

    private void SynchronizeScopes(IReadOnlyList<AutomationStepRecord> steps)
    {
        var scopes = new List<AutomationLogScope> { new("", "全部日志", []) };
        AddScopes(scopes, steps, "", "", []);
        if (scopes.Count == Scopes.Count && scopes.Zip(Scopes).All(pair => SameScope(pair.First, pair.Second))) return;
        var selected = SelectedScope;
        _changing = true;
        Scopes.Clear();
        foreach (var scope in scopes) Scopes.Add(scope);
        var updated = Scopes.FirstOrDefault(scope => scope.Key == selected?.Key) ?? Scopes[0];
        SelectedScope = updated;
        _changing = false;
        if (selected == null || !SameScope(selected, updated))
        {
            _endLine = null;
            _newerPages.Clear();
            Clear();
            Restart();
        }
    }

    private static bool SameScope(AutomationLogScope left, AutomationLogScope right) =>
        left.Key == right.Key && left.Path.Count == right.Path.Count
        && left.Path.Zip(right.Path).All(nodes => nodes.First.SetEquals(nodes.Second));

    internal void SelectScope(string key)
    {
        SelectedScope = Scopes.FirstOrDefault(scope => scope.Key == key) ?? Scopes.FirstOrDefault();
    }

    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active) Stop();
        else if (FollowLatest || Document.TextLength == 0 || !_caughtUp) Restart();
    }

    partial void OnSelectedScopeChanged(AutomationLogScope? value)
    {
        if (_changing) return;
        _endLine = null;
        _newerPages.Clear();
        Clear();
        Restart();
    }

    partial void OnFollowLatestChanged(bool value)
    {
        if (_changing) return;
        if (value) { _endLine = null; _newerPages.Clear(); Restart(); }
        else if (!IsLoading) Stop(); // Freeze the reading window while the user scrolls or searches.
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(CanLoadEarlier));
        OnPropertyChanged(nameof(CanLoadNewer));
    }

    [RelayCommand]
    private void LoadEarlier()
    {
        if (!CanLoadEarlier) return;
        FollowLatest = false;
        _newerPages.Push(_endLine);
        // Keep an overlap so scrolling can anchor to the same visible row after loading.
        _endLine = _firstLine + Math.Min(50, _lastLine - _firstLine);
        Restart();
    }

    [RelayCommand]
    private void LoadNewer()
    {
        if (!CanLoadNewer) return;
        _endLine = _newerPages.Count > 0 ? _newerPages.Pop()
            : Math.Min(_total, _lastLine + Math.Max(1, _lastLine - _firstLine - 50));
        Restart();
    }

    [RelayCommand]
    private void Latest()
    {
        _endLine = null;
        _newerPages.Clear();
        if (!FollowLatest) FollowLatest = true;
        else Restart();
    }

    [RelayCommand]
    private void Reload() => Restart();

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsExporting) return;
        var runId = _record?.RunId;
        IsExporting = true;
        try { await _export(); }
        catch (Exception ex) { if (_record?.RunId == runId) Notice = "导出失败：" + ex.Message; }
        finally { IsExporting = false; }
    }

    private void Clear()
    {
        Updating?.Invoke();
        Document.Text = "";
        _firstLine = _lastLine = _total = 0;
        Notice = "";
        NotifyRange();
        Updated?.Invoke(true, null);
    }

    private void Restart()
    {
        Stop();
        if (!_active || _record == null || SelectedScope == null) return;
        _reading = new CancellationTokenSource();
        _caughtUp = false;
        _ = ReadAsync(_record, SelectedScope, _endLine, _generation, _reading);
    }

    private async Task ReadAsync(AutomationRunRecord record, AutomationLogScope scope, long? endLine,
        int generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        IsLoading = true;
        using var reader = new AutomationLogReader(scope, record.Steps);
        try
        {
            var initial = true;
            while (true)
            {
                var snapshot = _record ?? record;
                var page = await Task.Run(() => reader.Read(snapshot, endLine, token), token);
                if (token.IsCancellationRequested || generation != _generation) return;
                // A failed run may have long recovery output after the error. Read the error's own window.
                if (initial && page.CaughtUp && endLine == null && !FollowLatest && snapshot.Status == AutomationRunStatus.Failed
                    && page.FailureLine is { } failure && page.Lines.FirstOrDefault()?.Number > failure)
                {
                    _endLine = failure + 100;
                    using var nearFailure = new AutomationLogReader(scope, snapshot.Steps);
                    page = await Task.Run(() =>
                    {
                        AutomationLogPage result;
                        do { result = nearFailure.Read(snapshot, failure + 100, token); } while (!result.CaughtUp);
                        return result;
                    }, token);
                    if (token.IsCancellationRequested || generation != _generation) return;
                }
                // Keep the current viewport intact while another window is being read.
                if (page.CaughtUp || Document.TextLength == 0) Apply(page, initial && page.CaughtUp);
                _caughtUp = page.CaughtUp;
                if (page.CaughtUp) initial = false;
                IsLoading = !page.CaughtUp;
                if (page.CaughtUp && (!FollowLatest || snapshot.IsFinished)) return;
                await Task.Delay(page.CaughtUp ? 600 : 16, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == _generation) Notice = "读取日志失败：" + ex.Message;
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
            if (ReferenceEquals(_reading, cancellation)) _reading = null;
            cancellation.Dispose();
        }
    }

    private void Apply(AutomationLogPage page, bool initial)
    {
        Notice = page.Notice;
        if (page.CaughtUp && page.Lines.Length == 0 && Notice.Length == 0) Notice = "当前范围没有日志。";
        var first = page.Lines.FirstOrDefault()?.Number ?? 0;
        var last = page.Lines.LastOrDefault()?.Number ?? 0;
        var reset = initial || (_lastLine == 0 && last > 0) || first > _lastLine + 1 || first < _firstLine || last < _lastLine;
        if (reset || first != _firstLine || last != _lastLine)
        {
            Updating?.Invoke();
            using (Document.RunUpdate())
            {
                if (reset) Document.Text = string.Join("\n", page.Lines.Select(line => line.Text)) + (last > 0 ? "\n" : "");
                else
                {
                    var remove = (int)(first - _firstLine);
                    if (remove > 0) Document.Remove(0, Document.GetLineByNumber(remove + 1).Offset);
                    var added = page.Lines.Where(line => line.Number > _lastLine).Select(line => line.Text).ToArray();
                    if (added.Length > 0) Document.Insert(Document.TextLength, string.Join("\n", added) + "\n");
                }
            }
            var focus = initial && !FollowLatest && page.FailureLine is { } failed && failed >= first && failed <= last
                ? (int?)(failed - first + 1) : null;
            Updated?.Invoke(reset, focus);
        }
        _firstLine = first;
        _lastLine = last;
        _total = page.Total;
        NotifyRange();
    }

    private void NotifyRange()
    {
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(CanLoadEarlier));
        OnPropertyChanged(nameof(CanLoadNewer));
    }

    private void Stop()
    {
        ++_generation;
        _reading?.Cancel();
        _reading = null;
        IsLoading = false;
    }

    public void Dispose() { _active = false; Stop(); }
}
