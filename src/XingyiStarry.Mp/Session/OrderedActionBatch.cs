using System.Collections.Generic;

namespace XingyiStarry.Mp.Session;

internal sealed class OrderedActionBatch
{
    private readonly long[] units;
    private readonly HashSet<long> started = new HashSet<long>();
    private readonly HashSet<long> completed = new HashSet<long>();
    public OrderedActionBatch(long[] units) => this.units = (long[])units.Clone();
    public void Start(long unit) => started.Add(unit);
    public void Complete(long unit) => completed.Add(unit);
    public bool Contains(long unit) => System.Array.IndexOf(units, unit) >= 0;
    public bool MayFinish(long unit)
    {
        if (started.Count != units.Length) return false;
        foreach (var earlier in units)
        {
            if (earlier == unit) return true;
            if (!completed.Contains(earlier)) return false;
        }
        return false;
    }
}
