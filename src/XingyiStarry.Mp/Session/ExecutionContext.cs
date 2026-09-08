using System;
using System.Threading;

namespace XingyiStarry.Mp.Session;

internal enum ExecutionOrigin { LocalInput, HostAuthority, ClientReplay, NestedOriginal }

internal static class ExecutionContext
{
    private static readonly AsyncLocal<ExecutionOrigin?> current = new AsyncLocal<ExecutionOrigin?>();
    public static ExecutionOrigin Origin => current.Value ?? ExecutionOrigin.LocalInput;
    public static bool IsAuthoritativeExecution => Origin == ExecutionOrigin.HostAuthority || Origin == ExecutionOrigin.ClientReplay || Origin == ExecutionOrigin.NestedOriginal;

    public static IDisposable Enter(ExecutionOrigin origin)
    {
        var previous = current.Value; current.Value = origin;
        return new Scope(() => current.Value = previous);
    }

    private sealed class Scope : IDisposable
    {
        private Action? exit;
        public Scope(Action exit) => this.exit = exit;
        public void Dispose() { var action = Interlocked.Exchange(ref exit, null); action?.Invoke(); }
    }
}
