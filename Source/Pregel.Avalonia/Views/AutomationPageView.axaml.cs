using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using Heron.Pregel.Avalonia.ViewModels;

namespace Heron.Pregel.Avalonia.Views;

public partial class AutomationPageView : UserControl
{
    private AutomationPageViewModel? _viewModel;
    private AutomationPagePanel? _settingsPanel;
    private bool _compactLayout;
    private bool _initialized;
    private string? _parameterTaskId;
    private const double CompactWidth = 900;

    public AutomationPageView()
    {
        InitializeComponent();
        _initialized = true;
        ActualThemeVariantChanged += (_, _) => _viewModel?.RefreshTheme();
        InitializeTaskReorder();
        RunDetailArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateRunLogHeight();
        };
        // 窄窗口判断看整个页面的可用宽度；设置展开时改为浮层，不挤压图。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateNarrowLayout();
        };
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (!_initialized) { base.OnDataContextChanged(e); return; }
        ResetTaskDrag();
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ReviewFocusRequested -= OnReviewFocusRequested;
        }
        _viewModel = DataContext as AutomationPageViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ReviewFocusRequested += OnReviewFocusRequested;
        }
        base.OnDataContextChanged(e);
        _viewModel?.RefreshTheme();
        UpdateNarrowLayout();
        UpdateModalState();
    }

    private void UpdateNarrowLayout()
    {
        if (Bounds.Width <= 0) return;
        if (_viewModel != null) _viewModel.IsNarrowLayout = Bounds.Width < AutomationPageViewModel.NarrowWidth;
        var compact = Bounds.Width < CompactWidth;
        RunGrid.ColumnDefinitions[0].Width = new GridLength(!compact && _viewModel?.IsRunsTab == true ? 188 : 0);
        RunHistoryPanel.IsVisible = _viewModel?.IsRunsTab == true;
        CompactTaskPopup.Width = Math.Min(340, Math.Max(240, Bounds.Width - 32));
        CompactHistoryPopup.Width = Math.Min(300, Math.Max(220, Bounds.Width - 32));
        CompactTaskPopup.Height = CompactHistoryPopup.Height = Math.Max(160, Math.Min(520, Bounds.Height - 64));
        if (compact == _compactLayout) return;
        _compactLayout = compact;
        CompactTaskPopup.IsOpen = CompactHistoryPopup.IsOpen = false;
        if (compact)
        {
            PageLayout.Children.Remove(TaskCard);
            RunGrid.Children.Remove(RunHistoryPanel);
            CompactTaskPopup.Child = TaskCard;
            CompactHistoryPopup.Child = RunHistoryPanel;
        }
        else
        {
            CompactTaskPopup.Child = CompactHistoryPopup.Child = null;
            PageLayout.Children.Add(TaskCard);
            RunGrid.Children.Add(RunHistoryPanel);
        }
        PageLayout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 272);
        PageLayout.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 1);
        PageLayout.Margin = new Thickness(0);
        RunHistoryPanel.BorderThickness = compact ? new Thickness(1) : new Thickness(0, 0, 1, 0);
        RunHistoryPanel.CornerRadius = new CornerRadius(compact ? 8 : 0);
        CompactNavigation.IsVisible = compact;
        Grid.SetColumn(TaskHeaderActions, compact ? 0 : 2);
        Grid.SetRow(TaskHeaderActions, compact ? 2 : 0);
        Grid.SetRowSpan(TaskHeaderActions, compact ? 1 : 2);
        Grid.SetColumnSpan(TaskHeaderActions, compact ? 3 : 1);
        TaskHeaderActions.Margin = new Thickness(0, compact ? 12 : 0, 0, 0);
        Grid.SetColumnSpan(TaskHeaderIdentity, compact ? 2 : 1);
    }

    private void OpenCompactTasks(object? sender, RoutedEventArgs e)
    {
        CompactHistoryPopup.IsOpen = false;
        CompactTaskPopup.IsOpen = !CompactTaskPopup.IsOpen;
    }

    private void OpenCompactHistory(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ViewHistoryCommand.Execute(null);
        CompactTaskPopup.IsOpen = false;
        CompactHistoryPopup.IsOpen = !CompactHistoryPopup.IsOpen;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutomationPageViewModel.PageTabIndex)) UpdateNarrowLayout();
        if (e.PropertyName == nameof(AutomationPageViewModel.SelectedParameterSection))
            Dispatcher.UIThread.Post(FocusParameterSection, DispatcherPriority.Loaded);
        if (e.PropertyName == nameof(AutomationPageViewModel.SelectedTask)
            && _parameterTaskId != _viewModel?.SelectedTask?.Id)
        {
            _parameterTaskId = _viewModel?.SelectedTask?.Id;
            if (SettingsScroll is { } scroll) scroll.Offset = default;
        }
        if (e.PropertyName is nameof(AutomationPageViewModel.IsTemplateMenuOpen) or nameof(AutomationPageViewModel.ShowScriptBrowser))
        {
            UpdateModalState();
            if (_viewModel is not { IsTemplateMenuOpen: true } and not { ShowScriptBrowser: true })
                RestoreTaskActionFocus(e.PropertyName == nameof(AutomationPageViewModel.IsTemplateMenuOpen)
                    ? NewTaskButton : ScriptBrowserButton);
        }
        if (e.PropertyName == nameof(AutomationPageViewModel.SelectedTask))
            CompactTaskPopup.IsOpen = CompactHistoryPopup.IsOpen = false;
        else if (e.PropertyName == nameof(AutomationPageViewModel.SelectedRun))
            CompactHistoryPopup.IsOpen = false;
        if (e.PropertyName != nameof(AutomationPageViewModel.SettingsFocusGroup)
            || _viewModel?.SettingsFocusGroup is not { } group) return;
        // 等展开动画开始、布局完成后再滚动，否则目标位置还是收起时的。
        DispatcherTimer.RunOnce(() => FocusSettingsGroup(group), TimeSpan.FromMilliseconds(200));
    }

    private void ParameterNavigation_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initialized) Dispatcher.UIThread.Post(FocusParameterSection, DispatcherPriority.Loaded);
    }

    private void FocusParameterSection()
    {
        if (!_initialized || _viewModel?.SelectedParameterSection is not { } item) return;
        Control? target = item.Key switch
        {
            AutomationPageViewModel.GroupSchedule => GroupSchedule,
            AutomationPageViewModel.GroupTask => GroupTask,
            "behavior" => GroupBehavior,
            "notification" => GroupNotification,
            _ => ParameterGroups?.GetVisualDescendants().OfType<Border>().FirstOrDefault(border =>
                border.Classes.Contains("parameterSection") && border.DataContext is AutomationParameterGroupViewModel group
                && item.Key == "parameter:" + group.Title)
        };
        if (SettingsScroll is not { } scroll || ParameterContent is not { } content
            || target?.TranslatePoint(default, content) is not { } point) return;
        scroll.Offset = new Vector(0, Math.Clamp(point.Y, 0,
            Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
    }

    private void OnReviewFocusRequested(object? sender, EventArgs e)
    {
        // 关闭子图后主图才重新可见，等布局完成再滚动。
        DispatcherTimer.RunOnce(() => ReviewPanel?.BringIntoView(), TimeSpan.FromMilliseconds(100));
    }

    private void FocusSettingsGroup(string group)
    {
        var target = group switch
        {
            AutomationPageViewModel.GroupTask => GroupTask,
            AutomationPageViewModel.GroupSchedule => GroupSchedule,
            _ => GroupOptions
        };
        if (_viewModel != null) _viewModel.SettingsFocusGroup = null;
        if (target == null) return;
        target.BringIntoView();
        target.Classes.Add("focused");
        DispatcherTimer.RunOnce(() => target.Classes.Remove("focused"), TimeSpan.FromMilliseconds(900));
    }

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // 内嵌弹窗统一接管自己的 Esc，不触发底层任务页的快捷键。
        if (GlobalSettingsDialog.IsOpen || _viewModel is { IsTemplateMenuOpen: true } or { ShowScriptBrowser: true }) return;
        if (CompactTaskPopup.IsOpen || CompactHistoryPopup.IsOpen)
        {
            CompactTaskPopup.IsOpen = CompactHistoryPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (RunLogs is { IsKeyboardFocusWithin: true } logs && logs.ClearSearch())
        {
            e.Handled = true;
            return;
        }
        if (_viewModel is { IsRunReportExpanded: true, ShowRunLogs: true })
        {
            _viewModel.CloseLogsCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (_viewModel is not { ShowSettingsOverlay: true }) return;
        _viewModel.CloseSettingsOverlayCommand.Execute(null);
        e.Handled = true;
    }

    private void SettingsOverlayDismiss_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel?.CloseSettingsOverlayCommand.Execute(null);
    }

    private void NewTaskButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AutomationPageViewModel vm) return;
        vm.IsTemplateMenuOpen = !vm.IsTemplateMenuOpen;
    }

    private void UpdateModalState()
    {
        var hasModal = GlobalSettingsDialog.IsOpen || _viewModel is { IsTemplateMenuOpen: true } or { ShowScriptBrowser: true };
        if (hasModal) CompactTaskPopup.IsOpen = CompactHistoryPopup.IsOpen = false;
        PageLayout.IsEnabled = !hasModal;
    }

    private void RestoreTaskActionFocus(Control button)
    {
        if (_compactLayout) CompactTaskButton.Focus();
        else button.Focus();
    }

    private void TemplatePicker_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm) return;
        if (e.Key == Key.Enter && vm.SelectedTemplate is { } template)
        {
            vm.NewTaskCommand.Execute(template);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ReferenceEquals(sender, TemplateSearchBox))
        {
            TemplateList.Focus();
            e.Handled = true;
        }
    }

    private void TemplateList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: AutomationTemplateOption template } && _viewModel is { } vm)
            vm.NewTaskCommand.Execute(template);
    }

    private void TaskList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not { IsUpdatingTaskList: false } vm) return;
        if (e.AddedItems.OfType<AutomationTaskItemViewModel>().FirstOrDefault() is { } task)
            vm.SelectedTask = task;
    }

    private async void ExpandParameterPreview_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AutomationEditorViewModel editor }
            || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new AutomationParameterPreviewWindow { DataContext = editor }.ShowDialog(owner);
    }

    private void GlobalSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (GlobalSettingsDialog.IsOpen || _viewModel?.Extensions.CreateSettingsPanel is not { } create) return;
        CompactTaskPopup.IsOpen = CompactHistoryPopup.IsOpen = false;
        var settings = _settingsPanel = create();
        settings.CloseRequested += CloseGlobalSettings;
        GlobalSettingsDialog.Content = settings.Content;
        GlobalSettingsDialog.InitialFocus = settings.InitialFocus;
        GlobalSettingsDialog.IsOpen = true;
        UpdateModalState();
    }

    private void CloseGlobalSettings(bool saved)
    {
        if (saved) _viewModel?.RefreshGlobalSettings();
        GlobalSettingsDialog.Close();
    }

    private void GlobalSettingsDialog_Closed(object? sender, EventArgs e)
    {
        if (_settingsPanel is { } settings) settings.CloseRequested -= CloseGlobalSettings;
        _settingsPanel = null;
        GlobalSettingsDialog.Content = null;
        GlobalSettingsDialog.InitialFocus = null;
        UpdateModalState();
        RestoreTaskActionFocus(GlobalSettingsButton);
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AutomationFieldViewModel field
            || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = field.Label,
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) field.Value = path;
    }
}
