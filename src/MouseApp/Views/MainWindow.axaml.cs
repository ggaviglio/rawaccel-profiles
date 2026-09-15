using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using System.Linq;

namespace MouseApp.Views;

public partial class MainWindow : Window
{
    private MouseApp.Models.MouseProfile? _currentlyEditing;

    public MainWindow()
    {
        InitializeComponent();

        // Two different minimums, deliberately not folded into one number:
        //
        //   The sidebar column's MinWidth keeps the ACTION BAR from being squeezed. The main
        //   content column is Auto, so it always takes its full natural width and the "*" sidebar
        //   column gets only what is left over -- the sidebar is the side that absorbs every
        //   squeeze. A column minimum is a layout constraint, re-enforced on every pass, so it
        //   holds at any window size and for any later change to what the content pane wants.
        //   Without it the bar was arranged 252 wide against the 266 it needs (8 * 28 + 7 * 6),
        //   and the buttons -- each pinned at MinWidth 28 by the Grid.Styles setter in the XAML --
        //   overlapped each other instead of shrinking.
        //
        //   Window.MinWidth keeps the CONTENT PANE from being clipped (ProfileEditorView's
        //   ScrollViewer sets HorizontalScrollBarVisibility="Disabled", so it clips rather than
        //   scrolling). That needs the content's real desired width, which only a genuine layout
        //   pass produces: a synthetic Measure() from here reports 473 against a real 487, and
        //   cannot reserve the scrollbar at all, because scrollbar visibility is settled during
        //   arrange, not measure. Hence the one-shot LayoutUpdated hook below.
        //
        // One-shot, NOT a live subscription: recomputing MinWidth on every layout change is what
        // forced the whole window wider during a vertical-only resize, reverted in 9e205f2.
        Opened += (_, _) =>
        {
            ActionBar.Measure(Size.Infinity);
            var sidebarPadding = SidebarHost.Padding.Left + SidebarHost.Padding.Right;
            var sidebarMinWidth = ActionBar.DesiredSize.Width + sidebarPadding;

            // SidebarHost's own column, looked up instead of hardcoded as [0].
            RootLayout.ColumnDefinitions[Grid.GetColumn(SidebarHost)].MinWidth = sidebarMinWidth;

            EventHandler? onFirstLayout = null;
            onFirstLayout = (_, _) =>
            {
                LayoutUpdated -= onFirstLayout;

                // The editor's vertical scrollbar takes real width in this theme and appears only
                // once the window is short enough, so the width layout just reported may or may not
                // already include it -- whichever it is, the minimum has to cover the case where it
                // does. Extent vs viewport is the signal for "is it in there"; the ScrollBar's own
                // IsVisible and Bounds read False/0 in both states and cannot be used. Its
                // thickness comes from the ScrollBar in the template (which is sized while
                // collapsed) rather than a hardcoded pixel buffer.
                //
                // Measured live at two starting heights; both converge on the same minimum:
                //     short window   desired 505, scrollbar already counted -> 505 + 0  + 278 = 783
                //     tall window    desired 487, scrollbar not yet counted -> 487 + 18 + 278 = 783
                var scrollViewer = MainContentHost.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                var scrollBarAlreadyCounted = scrollViewer is { } sv && sv.Extent.Height > sv.Viewport.Height;

                var verticalBar = MainContentHost.GetVisualDescendants()
                    .OfType<ScrollBar>()
                    .FirstOrDefault(b => b.Orientation == Orientation.Vertical);
                var scrollBarAllowance = !scrollBarAlreadyCounted && verticalBar is { } bar && !double.IsNaN(bar.Width)
                    ? bar.Width
                    : 0;

                MinWidth = MainContentHost.DesiredSize.Width + scrollBarAllowance + sidebarMinWidth;

                // App.axaml.cs restores the remembered window width before any of this runs, clamped
                // against the XAML MinWidth rather than the real one computed here -- and raising a
                // window's minimum does not widen a window that is already open. Without this the
                // app opens narrower than its own minimum and stays there, which is what made an
                // earlier fix to this number look like it had done nothing.
                if (ClientSize.Width < MinWidth)
                    Width = MinWidth;
            };
            LayoutUpdated += onFirstLayout;
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
