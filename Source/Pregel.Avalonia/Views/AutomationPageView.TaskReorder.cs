using System;
using System.Linq;
using Heron.Pregel.Avalonia.ViewModels;
using Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using System.Diagnostics;

namespace Heron.Pregel.Avalonia.Views;

public partial class AutomationPageView
{
    private static readonly Serilog.ILogger TaskDragLogger = Serilog.Log.ForContext<AutomationPageView>();
    private static readonly TimeSpan TaskHoldDuration = TimeSpan.FromMilliseconds(200);
    private const double TaskHoldMovementTolerance = 12;
    private IDisposable? _taskHoldTimer;
    private AutomationTaskItemViewModel? _pressedTask;
    private AutomationPageViewModel? _taskDragModel;
    private ListBoxItem? _taskDragVisual;
    private IPointer? _taskPointer;
    private Point _taskPressPoint;
    private bool _taskDragging;
    private bool _taskMoved;
    private long _taskPressedAt;
    private int _taskMoveEvents;

    private void InitializeTaskReorder()
    {
        var list = this.FindControl<ListBox>("TaskList")!;
        list.AddHandler(PointerPressedEvent, TaskPressed, RoutingStrategies.Tunnel);
        list.AddHandler(PointerMovedEvent, TaskMoved, RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, TaskReleased, RoutingStrategies.Tunnel);
        list.PointerCaptureLost += (_, e) =>
        {
            TaskDragLogger.Information("[TaskDrag] capture_lost active={Active} dragging={Dragging} pointer={Pointer} captured={Captured} source={Source}",
                _pressedTask?.Name, _taskDragging, e.Pointer.Id,
                e.Pointer.Captured?.GetType().Name, e.Source?.GetType().Name);
            ResetTaskDrag("capture-lost");
        };
        DetachedFromVisualTree += (_, _) => ResetTaskDrag("view-detached");
        TaskDragLogger.Information("[TaskDrag] handlers_attached list={List}", list.Name);
    }

    private static ListBoxItem? TaskItemAt(ListBox list, Point position) => list.GetVisualDescendants()
        .OfType<ListBoxItem>().FirstOrDefault(item => item.TranslatePoint(default, list) is { } origin
            && new Rect(origin, item.Bounds.Size).Contains(position));

    private void TaskPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || DataContext is not AutomationPageViewModel model
            || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        var item = TaskItemAt(list, e.GetPosition(list));
        if (item?.DataContext is not AutomationTaskItemViewModel task) return;
        if (!model.CanReorderTasks) return;
        ResetTaskDrag("new-press");
        TaskDragLogger.Information("[TaskDrag] pressed task={Task} draft={Draft} pointer={Pointer} type={Type} position={Position} captured={Captured}",
            task.Name, task.IsDraft, e.Pointer.Id, e.Pointer.Type, e.GetPosition(list),
            e.Pointer.Captured?.GetType().Name);
        if (task.IsDraft)
        {
            model.SelectedTask = task;
            e.Handled = true;
            TaskDragLogger.Information("[TaskDrag] pinned_selected task={Task}; drag disabled", task.Name);
            return;
        }
        _pressedTask = task;
        _taskDragModel = model;
        _taskPointer = e.Pointer;
        _taskPressPoint = e.GetPosition(list);
        _taskPressedAt = Stopwatch.GetTimestamp();
        _taskMoveEvents = 0;
        // Protect even the hold interval from a background discovery merge.
        model.BeginTaskReorder();
        e.Pointer.Capture(list);
        TaskDragLogger.Information("[TaskDrag] captured task={Task} pointer={Pointer} captured={Captured}",
            _pressedTask?.Name, e.Pointer.Id, e.Pointer.Captured?.GetType().Name);
        e.Handled = true;
        _taskHoldTimer = DispatcherTimer.RunOnce(() =>
        {
            if (_pressedTask == null) return;
            TaskDragLogger.Information("[TaskDrag] hold_triggered task={Task} elapsed_ms={Elapsed} pointer={Pointer} captured={Captured}",
                _pressedTask.Name, Stopwatch.GetElapsedTime(_taskPressedAt).TotalMilliseconds,
                _taskPointer?.Id, _taskPointer?.Captured?.GetType().Name);
            _taskDragging = true;
            _taskDragVisual = item;
            item.Classes.Add("reordering");
            list.Cursor = new Cursor(StandardCursorType.SizeAll);
        }, TaskHoldDuration);
    }

    private void TaskMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTask == null || sender is not ListBox list) return;
        if (_taskDragModel == null || !ReferenceEquals(DataContext, _taskDragModel))
        {
            ResetTaskDrag("data-context-changed-during-move");
            return;
        }
        var position = e.GetPosition(list);
        _taskMoveEvents++;
        if (!_taskDragging)
        {
            var dx = position.X - _taskPressPoint.X;
            var dy = position.Y - _taskPressPoint.Y;
            if (dx * dx + dy * dy > TaskHoldMovementTolerance * TaskHoldMovementTolerance)
            {
                TaskDragLogger.Information("[TaskDrag] hold_cancelled_by_motion task={Task} elapsed_ms={Elapsed} from={From} to={To}",
                    _pressedTask.Name, Stopwatch.GetElapsedTime(_taskPressedAt).TotalMilliseconds,
                    _taskPressPoint, position);
                ResetTaskDrag("moved-before-hold");
            }
            return;
        }
        var targetItem = TaskItemAt(list, position);
        if (targetItem?.DataContext is AutomationTaskItemViewModel target && target != _pressedTask
            && targetItem.TranslatePoint(default, list) is { } targetOrigin)
        {
            var from = _taskDragModel.Tasks.IndexOf(_pressedTask);
            var to = _taskDragModel.Tasks.IndexOf(target);
            var midpoint = targetOrigin.Y + targetItem.Bounds.Height / 2;
            // Crossing a card edge alone must not swap the rows back and forth.
            if (from > to && position.Y > midpoint || from < to && position.Y < midpoint) return;
            var started = Stopwatch.GetTimestamp();
            TaskDragLogger.Information("[TaskDrag] move_requested task={Task} target={Target} position={Position} captured={Captured}",
                _pressedTask.Name, target.Name, position, e.Pointer.Captured?.GetType().Name);
            var moved = _taskDragModel.MoveTask(_pressedTask, target);
            _taskMoved |= moved;
            TaskDragLogger.Information("[TaskDrag] move_finished moved={Moved} elapsed_ms={Elapsed}",
                moved, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        e.Handled = true;
    }

    private void TaskReleased(object? sender, PointerReleasedEventArgs e)
    {
        TaskDragLogger.Information("[TaskDrag] released task={Task} dragging={Dragging} moved={Moved} pointer={Pointer} captured={Captured}",
            _pressedTask?.Name, _taskDragging, _taskMoved, e.Pointer.Id,
            e.Pointer.Captured?.GetType().Name);
        if (_pressedTask == null) return;
        if (!_taskDragging && _taskDragModel is { } model && model.Tasks.Contains(_pressedTask))
            model.SelectedTask = _pressedTask;
        ResetTaskDrag("released");
        e.Handled = true;
    }

    private void ResetTaskDrag(string reason = "data-context-changed")
    {
        if (_pressedTask != null || _taskDragModel != null)
            TaskDragLogger.Information("[TaskDrag] reset reason={Reason} task={Task} dragging={Dragging} moved={Moved} move_events={Events} elapsed_ms={Elapsed}",
                reason, _pressedTask?.Name, _taskDragging, _taskMoved, _taskMoveEvents,
                Stopwatch.GetElapsedTime(_taskPressedAt).TotalMilliseconds);
        _taskHoldTimer?.Dispose();
        _taskHoldTimer = null;
        _taskDragVisual?.Classes.Remove("reordering");
        _taskDragVisual = null;
        var model = _taskDragModel;
        var moved = _taskMoved;
        var pointer = _taskPointer;
        _taskDragModel = null;
        _taskPointer = null;
        _pressedTask = null;
        _taskDragging = false;
        _taskMoved = false;
        if (this.FindControl<ListBox>("TaskList") is { } list) list.Cursor = null;
        model?.EndTaskReorder(moved);
        pointer?.Capture(null);
    }
}

