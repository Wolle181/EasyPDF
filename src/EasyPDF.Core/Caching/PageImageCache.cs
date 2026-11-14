using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace EasyPDF.Core.Caching;

/// <summary>
/// LRU cache with a byte budget. Entries marked as protected (visible pages) are never evicted,
/// so the budget can be exceeded temporarily. Not thread-safe (UI thread only).
/// </summary>
public sealed class PageImageCache<TKey, TValue>(long budgetBytes, Func<TValue, long> sizeOf)
    where TKey : notnull
    where TValue : IDisposable
{
    private sealed class Entry(TKey key, TValue value, long bytes)
    {
        public TKey Key { get; } = key;
        public TValue Value { get; } = value;
        public long Bytes { get; } = bytes;
    }

    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new(); // First = most recently used

    public long BudgetBytes { get; } = budgetBytes;

    public long UsedBytes { get; private set; }

    public int Count => _map.Count;

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            if (node != _lru.First)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
            value = node.Value.Value;
            return true;
        }
        value = default;
        return false;
    }

    public bool Contains(TKey key) => _map.ContainsKey(key);

    /// <summary>Stores a value (disposing a previous one for the same key) and trims to the budget.</summary>
    public void Set(TKey key, TValue value, Func<TKey, bool>? isProtected = null)
    {
        Remove(key);
        var entry = new Entry(key, value, sizeOf(value));
        _map[key] = _lru.AddFirst(entry);
        UsedBytes += entry.Bytes;
        Trim(isProtected);
    }

    public void Trim(Func<TKey, bool>? isProtected = null)
    {
        var node = _lru.Last;
        while (UsedBytes > BudgetBytes && node != null)
        {
            var prev = node.Previous;
            if (isProtected == null || !isProtected(node.Value.Key))
                Remove(node.Value.Key);
            node = prev;
        }
    }

    public bool Remove(TKey key)
    {
        if (!_map.Remove(key, out var node))
            return false;
        _lru.Remove(node);
        UsedBytes -= node.Value.Bytes;
        node.Value.Value.Dispose();
        return true;
    }

    public void Clear()
    {
        foreach (var entry in _lru)
            entry.Value.Dispose();
        _lru.Clear();
        _map.Clear();
        UsedBytes = 0;
    }
}
