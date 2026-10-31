using System;
using System.Collections;
using System.Collections.Generic;

namespace XingyiStarry.Mp.Session;

// Drive nested native iterators inside the caller's execution/error scope.
internal sealed class CoroutineStack : IDisposable
{
    private readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
    public object? Current { get; private set; }
    public CoroutineStack(IEnumerator root) => stack.Push(root);

    public bool MoveNext()
    {
        while (stack.Count != 0)
        {
            var top = stack.Peek();
            if (!top.MoveNext()) { stack.Pop(); (top as IDisposable)?.Dispose(); continue; }
            if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
            Current = top.Current;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        while (stack.Count != 0) (stack.Pop() as IDisposable)?.Dispose();
    }
}
