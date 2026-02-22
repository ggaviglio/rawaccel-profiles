using System;
using MouseApp.ViewModels;

namespace MouseApp.Services;

public class NavigationService
{
    private ViewModelBase? _current;
    public event Action<ViewModelBase?>? CurrentChanged;

    public ViewModelBase? Current
    {
        get => _current;
        private set
        {
            _current = value;
            CurrentChanged?.Invoke(_current);
        }
    }

    public void Navigate(ViewModelBase? vm) => Current = vm;
}
