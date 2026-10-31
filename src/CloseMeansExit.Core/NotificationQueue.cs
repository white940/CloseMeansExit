using System.Diagnostics.CodeAnalysis;

namespace CloseMeansExit.Core;

public sealed record ResidencyNotice(CloseObservation Observation, long WorkingSetBytes);

/// <summary>
/// Bounded FIFO of pending notices, coalesced by executable path. Removing a
/// notice also removes its deduplication entry so a future close can notify again.
/// </summary>
public sealed class NotificationQueue
{
    public const int Capacity = 50;

    private readonly object _gate = new();
    private readonly LinkedList<ResidencyNotice> _pending = new();
    private readonly Dictionary<string, LinkedListNode<ResidencyNotice>> _byPath =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <returns>True for a new queue entry; false when coalescing an existing path.</returns>
    public bool Enqueue(ResidencyNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(notice.Observation);
        ArgumentNullException.ThrowIfNull(notice.Observation.Process);
        string path = RulePaths.Normalize(notice.Observation.Process.ExecutablePath);

        lock (_gate)
        {
            if (_byPath.TryGetValue(path, out LinkedListNode<ResidencyNotice>? existing))
            {
                // Asynchronous observations can arrive out of order. Keep one
                // coherent observation/memory pair from the newest event.
                if (notice.Observation.At >= existing.Value.Observation.At)
                {
                    existing.Value = notice;
                }

                return false;
            }

            if (_pending.Count == Capacity)
            {
                RemoveFirst();
            }

            _byPath.Add(path, _pending.AddLast(notice));
            return true;
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out ResidencyNotice? notice)
    {
        lock (_gate)
        {
            if (_pending.First is not { } first)
            {
                notice = null;
                return false;
            }

            notice = first.Value;
            RemoveFirst();
            return true;
        }
    }

    public void Remove(string executablePath)
    {
        string path = RulePaths.Normalize(executablePath);

        lock (_gate)
        {
            if (_byPath.Remove(path, out LinkedListNode<ResidencyNotice>? node))
            {
                _pending.Remove(node);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _byPath.Clear();
        }
    }

    private void RemoveFirst()
    {
        LinkedListNode<ResidencyNotice> first = _pending.First!;
        _byPath.Remove(RulePaths.Normalize(first.Value.Observation.Process.ExecutablePath));
        _pending.RemoveFirst();
    }
}
