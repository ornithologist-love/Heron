using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Heron.ClientFramework.Abstractions;
using Heron.ClientFramework.Infrastructure;

namespace Heron.ClientFramework.Shell;

/// <summary>Footer resource entry kind; VRAM identifies GPU entries that may use a dedicated icon.</summary>
public enum MachineResourceKind
{
    Cpu,
    Memory,
    Vram,
    Disk,
}

public sealed record MachineResourceStatusItem(MachineResourceKind Kind, Icon Icon, string Text, string Detail, ICommand Command)
{
    public bool IsGpu => Kind == MachineResourceKind.Vram;
}

/// <summary>Periodically samples CPU, memory, VRAM, and disk stats via <see cref="SystemResourceMonitor" /> for shell footers.</summary>
public partial class MachineResourcesStatusViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly SystemResourceMonitor _monitor = new();
    private Task? _refresh;
    private bool _disposed;
    [ObservableProperty] private string _summary = "资源 · 正在读取…";
    [ObservableProperty] private string _details = "CPU 占用率；内存、显存与各本地磁盘的可用 / 总量；每 10 秒刷新。";
    [ObservableProperty] private IReadOnlyList<MachineResourceStatusItem> _items = [];

    public MachineResourcesStatusViewModel() => _timer.Tick += (_, _) => Refresh();

    public void Start()
    {
        if (_disposed) return;
        _timer.Start();
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (!_disposed && _refresh is not { IsCompleted: false }) _refresh = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _monitor.SampleAsync(_lifetime.Token), _lifetime.Token);
            if (_disposed) return;
            var rows = BuildRows(snapshot);
            Summary = string.Join("  ·  ", rows.Select(row => row.Raw));
            Items = rows.Select(row => new MachineResourceStatusItem(
                row.Kind, row.Icon, FormatStatusText(row.Raw), row.Detail, RefreshCommand)).ToArray();
            Details = "CPU 总占用率；其余为可用 / 总量（GB）\n"
                + string.Join("\n", rows.Select(row => row.Raw))
                + "\n每 10 秒刷新；未就绪磁盘不显示。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_disposed) return;
            Summary = "资源 · 暂时无法读取";
            Details = "点击重试，或等待下次自动刷新。";
            Items = [new(MachineResourceKind.Cpu, Icon.Desktop, Summary, Details, RefreshCommand)];
        }
    }

    private static ResourceRow[] BuildRows(SystemResourceSnapshot snapshot)
    {
        const string refreshHint = "点击刷新；每 10 秒自动更新。";
        var cpuRaw = snapshot.CpuUsagePercent is { } usage ? $"CPU {usage:0}%" : "CPU 未知";
        var memoryRaw = snapshot is { MemoryTotalGb: { } memoryTotal, MemoryAvailableGb: { } memoryAvailable }
            ? $"内存 {memoryAvailable:0.#}/{memoryTotal:0.#} GB 可用"
            : "内存 未知";
        var memoryDetail = snapshot is { MemoryTotalGb: { } memoryTotal2, MemoryAvailableGb: { } memoryAvailable2 }
            ? $"物理内存：可用 {memoryAvailable2:0.#} GB / 总量 {memoryTotal2:0.#} GB\n{refreshHint}"
            : $"物理内存读取失败\n{refreshHint}";
        var vramDetail = snapshot.Gpu.TotalGb is { } vramTotal
            ? snapshot.Gpu.FreeGb is { } vramFree
                ? $"{snapshot.Gpu.Model}：可用 {vramFree:0.#} GB / 总量 {vramTotal:0.#} GB\n{refreshHint}"
                : $"{snapshot.Gpu.Model}：总量 {vramTotal:0.#} GB（该驱动不支持读取实时余量）\n{refreshHint}"
            : $"未检测到可读取显存的显卡\n{refreshHint}";
        var diskDetail = snapshot.Disks.Count > 0
            ? "各磁盘可用 / 总量（GB）：\n"
              + string.Join("\n", snapshot.Disks.Select(FormatDiskDetailLine))
              + $"\n{refreshHint}"
            : $"未检测到固定或可移动磁盘\n{refreshHint}";
        return
        [
            new(MachineResourceKind.Cpu, Icon.DeveloperBoard, cpuRaw, $"CPU 总占用率\n{refreshHint}"),
            new(MachineResourceKind.Memory, Icon.Ram, memoryRaw, memoryDetail),
            new(MachineResourceKind.Vram, Icon.Desktop, FormatVramLabel(snapshot.Gpu), vramDetail),
            new(MachineResourceKind.Disk, Icon.HardDrive, FormatDiskSummary(snapshot.Disks), diskDetail),
        ];
    }

    public static string FormatVramLabel(GpuMemorySnapshot gpu)
    {
        if (gpu.TotalGb is not { } total) return "VRAM 未知";
        return gpu.FreeGb is { } free
            ? $"VRAM {free:0.#}/{total:0.#} GB 可用"
            : $"VRAM {total:0.#} GB";
    }

    public static string FormatDiskSummary(IReadOnlyList<DiskSpaceSnapshot> disks)
        => disks.Count == 0
            ? "未知"
            : $"{disks.Sum(disk => disk.FreeGb):0.#}/{disks.Sum(disk => disk.TotalGb):0.#} GB 可用";

    public static string FormatDiskDetailLine(DiskSpaceSnapshot disk)
        => $"{disk.Name} {disk.FreeGb:0.#}/{disk.TotalGb:0.#} 可用";

    public static string FormatStatusText(string value)
    {
        var text = value.Replace(" GB 可用", " GB");
        if (text.StartsWith("内存 ", StringComparison.Ordinal)) text = text[3..];
        return text.Replace("/", " GB / ");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _lifetime.Cancel();
        if (_refresh != null) await _refresh;
        _lifetime.Dispose();
    }

    private sealed record ResourceRow(MachineResourceKind Kind, Icon Icon, string Raw, string Detail);
}
