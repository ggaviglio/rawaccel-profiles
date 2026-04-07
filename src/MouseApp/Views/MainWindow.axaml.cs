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
        }
    }
}