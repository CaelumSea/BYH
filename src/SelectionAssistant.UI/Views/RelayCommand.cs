using System.Windows.Input;

namespace SelectionAssistant.UI.Views;

/// <summary>
/// Minimal ICommand for NativeAOT-safe button command binding. No reflection,
/// no canExecute-changed wiring (buttons just check CanExecute at bind time).
/// Compiled bindings resolve this concrete type's members without reflection.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
