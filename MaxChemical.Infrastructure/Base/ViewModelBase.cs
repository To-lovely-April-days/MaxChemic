
using Prism.Events;
using Prism.Mvvm;

namespace MaxChemical.Infrastructure.Base;

public abstract class ViewModelBase : BindableBase, IDisposable
{
    protected readonly IEventAggregator EventAggregator;
    private bool _disposed;

    protected ViewModelBase(IEventAggregator eventAggregator)
    {
        EventAggregator = eventAggregator;
    }

    protected virtual void OnDispose()
    {
        // Override in derived classes for cleanup
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            OnDispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    protected bool SetProperty<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        RaisePropertyChanged(propertyName);
        return true;
    }
}