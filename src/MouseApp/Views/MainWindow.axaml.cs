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
        // width plus the sidebar's own genuine minimum (the action bar at full size), so it
        // can't be squeezed so far that either side breaks.
        //
        // Deliberately a ONE-TIME read of MainContentHost.Bounds.Width, not a live
        // GetObservable/Subscribe -- a live subscription was tried and reverted: it recomputes
        // MinWidth on every change to MainContentHost's *arranged* Bounds, and
        // ProfileEditorView's own vertical ScrollViewer (VerticalScrollBarVisibility="Auto")
        // widens its arranged bounds slightly to make room for its scrollbar track whenever a
        // vertical resize makes it appear -- which the live subscription immediately promoted
        // into a bigger MinWidth, forcing the whole window wider on a vertical-only resize.
        //
        // A second bug was found immediately after fixing the first one: MainContentHost.Bounds.Width
        // read here reflects whatever's true at Opened time, before the main content's own vertical
        // ScrollViewer (VerticalScrollBarVisibility="Auto") has necessarily decided it needs its
        // scrollbar. If that scrollbar appears later (e.g. once the window is resized shorter), the
        // extra width it claims comes straight out of the sidebar, pushing the action bar below the
        // exact 28px-per-button floor sidebarMinWidth is supposed to guarantee -- so the buttons
        // overflowed their row instead of shrinking gracefully.
        //
        // Fixed by measuring MainContentHost with an artificially tiny available height (1px) --
        // far less than its content could ever fit -- which forces its ScrollViewer to decide,
        // during this one Measure call, that it needs the vertical scrollbar. The resulting
        // DesiredSize.Width therefore already includes that theme's real scrollbar track width, a
        // measured number rather than a guessed pixel buffer. Real subsequent layout (with the
        // window's actual height) re-measures normally afterward and shows the scrollbar only when
        // actually needed -- this call is solely to read a worst-case width, same as ActionBar's own
        // Measure(Size.Infinity) below is solely to read its natural width.
        Opened += (_, _) =>
        {
            ActionBar.Measure(Size.Infinity);
            var sidebarPadding = SidebarHost.Padding.Left + SidebarHost.Padding.Right;
            var sidebarMinWidth = ActionBar.DesiredSize.Width + sidebarPadding;

            MainContentHost.Measure(new Size(double.PositiveInfinity, 1));
            var contentWidthWithScrollbar = MainContentHost.DesiredSize.Width;

            MinWidth = contentWidthWithScrollbar + sidebarMinWidth;
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