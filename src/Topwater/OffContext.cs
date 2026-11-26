namespace Topwater;

/// <summary>
/// Public async entry points run through here. Called from a thread with a synchronization context (WinForms, WPF, MAUI,
/// Avalonia), the work moves to the thread pool: a host that blocks on the task must not deadlock, the interpreter must not
/// freeze the UI, and host commands must not capture the context. Without one it runs as is.
/// </summary>
internal static class OffContext
{
    static bool OnContext => SynchronizationContext.Current is not null || TaskScheduler.Current != TaskScheduler.Default;

    public static Task<T> Run<T>(Func<Task<T>> body) => OnContext ? Task.Run(body) : body();
    public static Task Run(Func<Task> body) => OnContext ? Task.Run(body) : body();
}
