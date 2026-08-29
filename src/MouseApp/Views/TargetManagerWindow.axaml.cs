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
        //
        // The default Width is likewise no longer a separately hardcoded guess -- BottomRow's
        // content is the one thing here with a real, stable natural width, so the window opens
        // at that same computed floor instead of a separate guessed default.
        //
        // MinHeight is a genuinely separate, larger number on purpose: this floor alone only
        // guarantees CenterColumn (the invisible header spacer + Add as EXE/Path/Class/Title
        // stack) never gets clipped. A Grid's "*" row does not automatically protect its tallest
        // child from being compressed below its own natural size just because they share a row,
        // so without this explicit MinHeight the buttons genuinely squish once the window gets
        // small enough. The two ListBoxes don't factor in here since they degrade by scrolling,
        // not clipping.
        //
        // This measuring/resizing runs here, in the constructor, right after InitializeComponent
        // -- not in Opened. It was in Opened originally, resizing the window after it had already
        // been shown and centered once by WindowStartupLocation="CenterOwner" (which only centers
        // at that one moment); that left the window visibly flashing at the wrong position for a
        // frame before a manual re-center corrected it. Measuring here instead means the window's
        // final size is already known before it's ever shown, so CenterOwner's own one-time
        // centering just uses the right size the first time -- no flash, no manual repositioning
        // needed. This only works because BottomRow/CenterColumn/BottomRowContainer are part of
        // this window's own logical tree built by InitializeComponent(), which is enough for
        // their styles to resolve and Measure() to return real numbers even though the window
        // itself isn't shown yet.
        BottomRow.Measure(Size.Infinity);
        var outerMargin = 12 * 2; // the root Grid's Margin="12", left + right
        var contentFitWidth = BottomRow.DesiredSize.Width + outerMargin;

        CenterColumn.Measure(Size.Infinity);
        BottomRowContainer.Measure(Size.Infinity);
        var mainPaneBottomMargin = 10; // the 3-column pane's own Margin="0,0,0,10"
        var outerVerticalMargin = 12 * 2; // the root Grid's Margin="12", top + bottom
        var contentFitMinHeight = CenterColumn.DesiredSize.Height
            + mainPaneBottomMargin
            + BottomRowContainer.DesiredSize.Height
            + outerVerticalMargin;

        MinWidth = contentFitWidth;
        Width = contentFitWidth;
        Height = contentFitWidth * 2 / 3;
        MinHeight = contentFitMinHeight;
    }

    public TargetManagerWindow(TargetManagerViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
    }
}
