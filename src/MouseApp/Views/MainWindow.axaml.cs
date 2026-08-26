using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MouseApp.Views;

public partial class MainWindow : Window
{
    private MouseApp.Models.MouseProfile? _currentlyEditing;

    public MainWindow()
    {
        InitializeComponent();

        // The main content column is Auto-sized to its own natural width so growing the
        // window only widens the sidebar. Floor the window's width at that natural content
        // width plus the sidebar's own genuine minimum, so it can't be squeezed so far that
        // either side breaks. The sidebar's action-bar buttons declare MinWidth="28" each
        // (a real usability floor, not a guess) via their style — measuring that here means
        // the window's minimum is derived from an actual constraint, not another guess.
        //
        // sidebarMinWidth itself is a one-time calculation (the action bar's own buttons never
        // change at runtime, so this part can't go stale), but MainContentHost's natural width
        // is NOT static -- it depends on the Profile Editor's card content (its CardWidth
        // SharedSizeGroup, match-percent text that can grow on an overflow value, etc.). The old
        // code read MainContentHost.Bounds.Width once, here in Opened, and never again; found
        // live that this let MinWidth go stale while the action bar's own equal-* columns keep
        // resizing to whatever's actually available right now, so the two sides could visibly
        // drift out of sync after the content column's width changed post-launch. Subscribing to
        // MainContentHost's Bounds instead keeps MinWidth continuously accurate. GetObservable
        // emits the current value immediately on subscribe, so this also covers the initial
        // assignment the old code did separately.
        Opened += (_, _) =>
        {
            ActionBar.Measure(Size.Infinity);
            var sidebarPadding = SidebarHost.Padding.Left + SidebarHost.Padding.Right;
            var sidebarMinWidth = ActionBar.DesiredSize.Width + sidebarPadding;

            MainContentHost.GetObservable(Visual.BoundsProperty)
                .Subscribe(new AnonymousObserver<Rect>(bounds => MinWidth = bounds.Width + sidebarMinWidth));
        };
    }

    private void ProfileName_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TextBlock tb && tb.DataContext is MouseApp.Models.MouseProfile profile)
        {
            if (_currentlyEditing != null && _currentlyEditing != profile)
                _currentlyEditing.IsEditing = false;

            _currentlyEditing = profile;
            profile.IsEditing = true;
        }
    }

    private void ProfileNameEdit_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.GetObservable(Visual.IsVisibleProperty).Subscribe(new IsVisibleObserver(tb));
        }
    }

    /// <summary>Generic <see cref="IObserver{T}"/> adapter so a plain lambda can be passed to
    /// <c>Subscribe</c> -- Avalonia's own <see cref="IObservable{T}"/> here doesn't carry the
    /// Rx.NET <c>Subscribe(Action&lt;T&gt;)</c> extension this codebase doesn't otherwise
    /// reference, so this is the same pattern <see cref="IsVisibleObserver"/> below already
    /// uses, just reusable for any value type instead of one written per call site.</summary>
    private sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class IsVisibleObserver(TextBox tb) : IObserver<bool>
    {
        public void OnNext(bool visible)
        {
            if (visible)
            {
                tb.Focus(NavigationMethod.Pointer);
                tb.SelectAll();
            }
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private void ProfileNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is MouseApp.Models.MouseProfile profile)
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                profile.IsEditing = false;
                _currentlyEditing = null;
                e.Handled = true;

                // Escape doesn't save: it's a cancel, though since the TwoWay binding already
                // applied the partially-typed text live, there's no clean "revert" available
                // either way -- matches this handler's pre-existing behavior, just now made
                // explicit instead of accidentally not-saving via a missing call.
                if (e.Key == Key.Enter && DataContext is ViewModels.MainWindowViewModel vm)
                    vm.SaveProfiles();
            }
        }
    }

    private void ProfileNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is MouseApp.Models.MouseProfile profile)
        {
            profile.IsEditing = false;
            if (_currentlyEditing == profile)
                _currentlyEditing = null;

            if (DataContext is ViewModels.MainWindowViewModel vm)
                vm.SaveProfiles();
        }
    }
}