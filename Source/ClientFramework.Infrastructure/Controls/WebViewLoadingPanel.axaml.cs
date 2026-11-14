using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>The shared embedded WebView loading mask. Progress is presentation only.</summary>
public partial class WebViewLoadingPanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private IDisposable? _revealTimer;
    private double _progress;
    private bool _completed;

    public event Action? RevealRequested;

    public WebViewLoadingPanel()
    {
        InitializeComponent();
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _revealTimer?.Dispose();
        _revealTimer = null;
        _completed = false;
        _progress = 0;
        IsVisible = true;
        UpdateProgress();
        _timer.Start();
    }

    public void Complete()
    {
        _completed = true;
        if (IsVisible) _timer.Start();
    }

    public void Fail()
    {
        Stop();
        IsVisible = true;
        LoadingPercent.Text = "加载失败";
    }

    public void Stop()
    {
        _timer.Stop();
        _revealTimer?.Dispose();
        _revealTimer = null;
    }

    private void OnTick(object? sender, EventArgs args)
    {
        _progress = _completed ? Math.Min(100, _progress + 12)
            : Math.Min(92, _progress + Math.Max(0.3, (92 - _progress) * 0.08));
        UpdateProgress();
        if (_progress < 100) return;
        _timer.Stop();
        _revealTimer = DispatcherTimer.RunOnce(() =>
        {
            _revealTimer = null;
            IsVisible = false;
            RevealRequested?.Invoke();
        }, TimeSpan.FromMilliseconds(200));
    }

    private void UpdateProgress()
    {
        LoadingProgressBar.Value = _progress;
        LoadingPercent.Text = $"{(int)_progress}%";
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
