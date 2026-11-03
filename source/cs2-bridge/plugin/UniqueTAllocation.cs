using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MinecraftBridge;

/// <summary>Assigns distinct skin indices to full controller handles.</summary>
public sealed class UniqueTAllocation
{
    readonly int capacity;
    readonly Dictionary<uint, int> assignments = new();

    public UniqueTAllocation(int capacity)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        Assignments = new ReadOnlyDictionary<uint, int>(assignments);
    }

    public IReadOnlyDictionary<uint, int> Assignments { get; }

    /// <summary>
    /// Retains live identities and gives newcomers the lowest free skin indices.
    /// Returns false without changing assignments when distinct live handles exceed capacity.
    /// The caller includes dead controllers that should retain their assignments.
    /// </summary>
    public bool Reconcile(IEnumerable<uint> liveControllerHandles)
    {
        ArgumentNullException.ThrowIfNull(liveControllerHandles);
        uint[] live = liveControllerHandles.Distinct().OrderBy(handle => handle).ToArray();
        if (live.Length > capacity) return false;

        var next = new Dictionary<uint, int>(live.Length);
        var used = new HashSet<int>();
        foreach (uint handle in live)
        {
            if (!assignments.TryGetValue(handle, out int index)) continue;
            next.Add(handle, index);
            used.Add(index);
        }

        int free = 0;
        foreach (uint handle in live)
        {
            if (next.ContainsKey(handle)) continue;
            while (used.Contains(free)) free++;
            next.Add(handle, free);
            used.Add(free++);
        }

        if (assignments.Count == next.Count &&
            next.All(pair => assignments.TryGetValue(pair.Key, out int index) && index == pair.Value))
            return true;

        assignments.Clear();
        foreach (uint handle in live) assignments.Add(handle, next[handle]);
        return true;
    }

    public void Clear() => assignments.Clear();
}
