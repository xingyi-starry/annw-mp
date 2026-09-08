using System;
using System.Collections.Generic;
using XingyiStarry.Mp.Protocol;

namespace XingyiStarry.Mp.Session;

internal sealed class OperationQueue
{
    private readonly Queue<CommandRequest> pending = new Queue<CommandRequest>();
    private readonly HashSet<string> seenRequests = new HashSet<string>(StringComparer.Ordinal);
    public bool IsExecuting { get; private set; }

    public bool Enqueue(CommandRequest request)
    {
        var key = request.ClientId.ToString("N") + ":" + request.RequestId;
        if (!seenRequests.Add(key)) return false;
        pending.Enqueue(request); return true;
    }

    public bool TryBegin(out CommandRequest? request)
    {
        if (IsExecuting || pending.Count == 0) { request = null; return false; }
        IsExecuting = true; request = pending.Dequeue(); return true;
    }

    public void Complete()
    {
        if (!IsExecuting) throw new InvalidOperationException("There is no executing operation.");
        IsExecuting = false;
    }
}
