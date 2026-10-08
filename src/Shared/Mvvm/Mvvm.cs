using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ClassroomControl.Shared.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Runs UI updates on the UI thread (WPF Dispatcher in the app; immediate in tests).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Command that runs an async action, ignores clicks while it runs, and reports failures instead of crashing the app.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private int _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync().ConfigureAwait(true);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public async Task ExecuteAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or by shutdown.
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            RaiseCanExecuteChanged();
        }
    }
}
