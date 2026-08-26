using Avalonia;
using Avalonia.Controls;
using MouseApp.ViewModels;

namespace MouseApp.Views;

public partial class TargetManagerWindow : Window
{
    public TargetManagerWindow()
    {
        InitializeComponent();

        // MinWidth used to be a flat hardcoded guess (700) instead of derived from the bottom
        // row's actual content -- found live: it went stale the moment that row's content
        // changed (widening the manual-entry TextBox), letting the window shrink small enough
        // for the Add button to crowd into Cancel/Save. Unlike MainWindow's equivalent fix, this
        // only needs a one-time measurement, not a live subscription: every element in BottomRow
        // (the ComboBox/TextBox widths, the button labels) is fixed at design time and never
        // resizes after the window opens, so its natural minimum width never changes at runtime.
        Opened += (_, _) =>
        {
            BottomRow.Measure(Size.Infinity);
            var outerMargin = 12 * 2; // the root Grid's Margin="12", left + right
            MinWidth = BottomRow.DesiredSize.Width + outerMargin;
        };
    }

    public TargetManagerWindow(TargetManagerViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
    }
}
