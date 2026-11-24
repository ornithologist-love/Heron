using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Persists and restores a window's bounds across displays and DPI scales. Fits the initial
///     size to the active screen's working area and falls back to primary-screen centering when
///     saved coordinates no longer overlap any connected monitor.
/// </summary>
public sealed class WindowPlacement
{
    public const double DefaultMinimumWidth = 1024;
    public const double DefaultMinimumHeight = 640;

    private const double ScreenWidthShare = 0.8;
    private const double ScreenHeightShare = 0.85;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Window _window;
    private readonly string _stateFile;
    private readonly double _preferredWidth;
    private readonly double _preferredHeight;
    private readonly double _minWidth;
    private readonly double _minHeight;
    private PixelPoint _normalPosition;
    private Size _normalSize;

    private WindowPlacement(Window window, string stateFile, double preferredWidth, double preferredHeight, double minWidth, double minHeight)
    {
        _window = window;
        _stateFile = stateFile;
        _preferredWidth = preferredWidth;
        _preferredHeight = preferredHeight;
        _minWidth = minWidth;
        _minHeight = minHeight;
    }

    /// <summary>Applies initial sizing/position before the window is shown and tracks normal-state bounds.</summary>
    public static WindowPlacement Attach(
        Window window,
        string stateFilePath,
        double preferredWidth = 1440,
        double preferredHeight = 960,
        double minWidth = DefaultMinimumWidth,
        double minHeight = DefaultMinimumHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (string.IsNullOrWhiteSpace(stateFilePath))
            throw new ArgumentException("State file path must not be empty.", nameof(stateFilePath));

        var placement = new WindowPlacement(window, stateFilePath, preferredWidth, preferredHeight, minWidth, minHeight);
        placement.Apply();
        window.PositionChanged += (_, _) => placement.TrackPosition();
        window.Resized += (_, _) => placement.TrackSize();
        return placement;
    }

    /// <summary>Saves normal-state bounds and maximized flag; IO errors are logged and never block close.</summary>
    public void Save()
    {
        if (_normalSize.Width <= 0 || _normalSize.Height <= 0) return;
        try
        {
            var state = new SavedPlacement
            {
                X = _normalPosition.X,
                Y = _normalPosition.Y,
                Width = Math.Round(_normalSize.Width),
                Height = Math.Round(_normalSize.Height),
                Maximized = _window.WindowState == WindowState.Maximized,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var temporary = _stateFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, _stateFile, overwrite: true);
        }
        catch (Exception error)
        {
            Serilog.Log.ForContext("SourceContext", "Window").Warning(error, "Main window placement could not be saved");
        }
    }

    private void Apply()
    {
        var screens = _window.Screens;
        var saved = Load();
        var screen = saved != null
            ? screens?.ScreenFromPoint(new PixelPoint(saved.X + 120, saved.Y + 20))
            : null;
        var restorePosition = screen != null;
        screen ??= screens?.Primary ?? screens?.All.FirstOrDefault();
        if (screen == null)
        {
            _window.MinWidth = _minWidth;
            _window.MinHeight = _minHeight;
            return;
        }

        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        var areaWidth = area.Width / scale;
        var areaHeight = area.Height / scale;

        _window.MinWidth = Math.Min(_minWidth, areaWidth);
        _window.MinHeight = Math.Min(_minHeight, areaHeight);

        if (saved is { Maximized: false } && saved.Width >= areaWidth - 1 && saved.Height >= areaHeight - 1)
        {
            saved = null;
            restorePosition = false;
        }

        double width, height;
        if (saved != null)
        {
            width = Math.Clamp(saved.Width, _window.MinWidth, areaWidth);
            height = Math.Clamp(saved.Height, _window.MinHeight, areaHeight);
        }
        else
        {
            width = Math.Clamp(Math.Round(areaWidth * ScreenWidthShare), _window.MinWidth, _preferredWidth);
            height = Math.Clamp(Math.Round(areaHeight * ScreenHeightShare), _window.MinHeight, _preferredHeight);
        }

        var pixelWidth = (int)Math.Round(width * scale);
        var pixelHeight = (int)Math.Round(height * scale);
        var x = restorePosition ? saved!.X : area.X + (area.Width - pixelWidth) / 2;
        var y = restorePosition ? saved!.Y : area.Y + (area.Height - pixelHeight) / 2;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - pixelWidth));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - pixelHeight));

        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Width = width;
        _window.Height = height;
        _window.Position = new PixelPoint(x, y);
        _normalPosition = _window.Position;
        _normalSize = new Size(width, height);
        if (saved?.Maximized == true) _window.WindowState = WindowState.Maximized;
    }

    private void TrackPosition() => Dispatcher.UIThread.Post(CaptureNormalBounds);

    private void TrackSize() => Dispatcher.UIThread.Post(CaptureNormalBounds);

    private void CaptureNormalBounds()
    {
        if (_window.WindowState != WindowState.Normal) return;
        _normalPosition = _window.Position;
        var clientSize = _window.ClientSize;
        if (clientSize.Width > 0 && clientSize.Height > 0) _normalSize = clientSize;
    }

    private SavedPlacement? Load()
    {
        try
        {
            if (!File.Exists(_stateFile)) return null;
            var state = JsonSerializer.Deserialize<SavedPlacement>(File.ReadAllText(_stateFile), JsonOptions);
            return state is { Width: > 0, Height: > 0 } ? state : null;
        }
        catch (Exception error)
        {
            Serilog.Log.ForContext("SourceContext", "Window").Warning(error, "Main window placement could not be read; using screen defaults");
            return null;
        }
    }

    private sealed class SavedPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }
}
