using System.Numerics;

namespace AAEmu.Game.Models.CryEngine.Loaders;

/// <summary>
/// Exact nearest-point index over the points of a single loaded 256 m path-block
/// <see cref="BaseBaiLoader"/>. Points are bucketed into uniform 64 m world-space
/// cells; queries spiral outward ring-by-ring and stop as soon as no unexamined
/// ring could contain a closer point, which always returns the true nearest point
/// at a fraction of the cost of scanning every node of the block.
/// </summary>
/// <remarks>
/// Built lazily per loaded block (never for the whole world at once) and immutable
/// after <see cref="Build"/>, so concurrent readers are safe once published.
/// </remarks>
public sealed class BaiPointGrid<T>(Func<T, Vector3> posOf)
{
    public const float BucketSize = 64f;

    private readonly struct Entry(Vector3 pos, T item)
    {
        public Vector3 Pos { get; } = pos;
        public T Item { get; } = item;
    }

    private readonly Dictionary<(int X, int Y), List<Entry>> _buckets = [];
    private readonly Func<T, Vector3> _posOf = posOf;
    private int _maxBucketSpread;

    /// <summary>Number of indexed points.</summary>
    public int Count { get; private set; }

    public void Add(T item)
    {
        var pos = _posOf(item);
        var key = ((int)MathF.Floor(pos.X / BucketSize), (int)MathF.Floor(pos.Y / BucketSize));
        if (!_buckets.TryGetValue(key, out var bucket))
            _buckets[key] = bucket = [];
        bucket.Add(new Entry(pos, item));
        Count++;
    }

    /// <summary>
    /// Freezes the index for querying and records the bucket spread used to bound
    /// the worst-case ring search over sparse regions.
    /// </summary>
    public void Build()
    {
        var spread = 0;
        foreach (var (x, y) in _buckets.Keys)
            spread = Math.Max(spread, Math.Max(Math.Abs(x), Math.Abs(y)));
        _maxBucketSpread = spread + 1;
    }

    /// <summary>
    /// Finds the indexed point nearest to <paramref name="pos"/> (Euclidean,
    /// exact minimum). Returns <paramref name="nearestDistance"/> as
    /// <see cref="float.MaxValue"/> when the grid holds no points.
    /// </summary>
    public T FindNearest(Vector3 pos, out float nearestDistance)
    {
        nearestDistance = float.MaxValue;
        if (Count == 0)
            return default;

        var cx = (int)MathF.Floor(pos.X / BucketSize);
        var cy = (int)MathF.Floor(pos.Y / BucketSize);
        T bestItem = default;
        var bestDistance = float.MaxValue;

        // Any point in an unexamined ring r lies at least (r - 1) * BucketSize away,
        // so once bestDistance <= ring * BucketSize no remaining point can be closer.
        for (var ring = 0; ring <= _maxBucketSpread; ring++)
        {
            for (var gx = cx - ring; gx <= cx + ring; gx++)
            for (var gy = cy - ring; gy <= cy + ring; gy++)
            {
                if (Math.Max(Math.Abs(gx - cx), Math.Abs(gy - cy)) != ring)
                    continue;
                if (!_buckets.TryGetValue((gx, gy), out var bucket))
                    continue;
                foreach (var entry in bucket)
                {
                    var distance = Vector3.Distance(entry.Pos, pos);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestItem = entry.Item;
                    }
                }
            }

            if (bestDistance <= ring * BucketSize)
                break;
        }

        nearestDistance = bestDistance;
        return bestItem;
    }
}
