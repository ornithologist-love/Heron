using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Heron.ClientFramework.Abstractions;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Tracks native page visibility without owning the feature's background services.</summary>
public static class NativePageActivity
{
    public static IDisposable Bind(Control view) => new Binding(view);

    private sealed class Binding : IDisposable
    {
        private readonly Control _view;
        private readonly List<Visual> _ancestors = [];
        private IClientPageActivity? _target;
        private bool _attached, _active;

        public Binding(Control view)
        {
            _view = view;
            _target = view as IClientPageActivity ?? view.DataContext as IClientPageActivity;
            view.AttachedToVisualTree += Attached;
            view.DetachedFromVisualTree += Detached;
            view.DataContextChanged += ContextChanged;
            if (view.IsAttachedToVisualTree()) Attach();
        }

        private void Attached(object? sender, VisualTreeAttachmentEventArgs e) => Attach();
        private void Attach()
        {
            Unobserve();
            _attached = true;
            _ancestors.Add(_view);
            _ancestors.AddRange(_view.GetVisualAncestors());
            foreach (var visual in _ancestors) visual.PropertyChanged += VisibilityChanged;
            Update();
        }

        private void Detached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _attached = false;
            Update();
            Unobserve();
        }

        private void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Visual.OpacityProperty
                || e.Property == Window.WindowStateProperty) Update();
        }

        private void ContextChanged(object? sender, EventArgs e)
        {
            if (_active) _target?.SetPageActive(false);
            _active = false;
            _target = _view as IClientPageActivity ?? _view.DataContext as IClientPageActivity;
            Update();
        }

        private void Update()
        {
            var active = _attached && _ancestors.All(visual => visual.IsVisible && visual.Opacity > 0
                && visual is not Window { WindowState: WindowState.Minimized });
            if (active == _active) return;
            _active = active;
            _target?.SetPageActive(active);
        }

        private void Unobserve()
        {
            foreach (var visual in _ancestors) visual.PropertyChanged -= VisibilityChanged;
            _ancestors.Clear();
        }

        public void Dispose()
        {
            _attached = false;
            Update();
            Unobserve();
            _view.AttachedToVisualTree -= Attached;
            _view.DetachedFromVisualTree -= Detached;
            _view.DataContextChanged -= ContextChanged;
        }
    }
}
