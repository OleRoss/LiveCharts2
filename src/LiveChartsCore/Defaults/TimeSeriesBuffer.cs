// The MIT License(MIT)
//
// Copyright(c) 2021 Alberto Rodriguez Orozco & LiveCharts Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.


using System;
using System.Collections.Generic;

namespace LiveChartsCore.Defaults;

/// <summary>
/// An experimental append-only Cartesian data source with cached mapping and indexed extrema.
/// X must be finite and nondecreasing; duplicate X values are allowed. Y must be finite or NaN
/// (a gap). Model mutations are not observed: coordinates are snapshots taken when appended.
/// The caller must serialize appends, clearing and queries, for example on the UI thread.
/// </summary>
/// <typeparam name="T">The original measurement model retained for tooltips.</typeparam>
public sealed class TimeSeriesBuffer<T>
{
    private const int ChunkShift = 12;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;
    private const int BlockSize = 8;
    private readonly Func<T, double> _xSelector;
    private readonly Func<T, double> _ySelector;
    private readonly List<Chunk> _chunks = new();
    // Level zero contains complete 8-sample blocks. Each higher level merges pairs.
    // Only completed nodes are stored, so an append never rebuilds an existing tree.
    private readonly List<List<Summary>> _levels = new();
    private Summary _pending = Summary.Empty;
    private Summary _all = Summary.Empty;

    /// <summary>Creates a buffer. Selectors run once per appended sample.</summary>
    public TimeSeriesBuffer(Func<T, double> xSelector, Func<T, double> ySelector)
    {
        _xSelector = xSelector ?? throw new ArgumentNullException(nameof(xSelector));
        _ySelector = ySelector ?? throw new ArgumentNullException(nameof(ySelector));
    }

    /// <summary>Gets the number of retained samples.</summary>
    public int Count { get; private set; }

    /// <summary>Gets a revision that changes after every append or clear.</summary>
    public long Version { get; private set; }

    internal long Generation { get; private set; }

    /// <summary>Appends a sample and caches its coordinates.</summary>
    public void Append(T model)
    {
        var x = _xSelector(model);
        var y = _ySelector(model);
        if (double.IsNaN(x) || double.IsInfinity(x) || (Count != 0 && x < X(Count - 1)))
            throw new ArgumentException("X must be finite and nondecreasing.", nameof(model));
        if (double.IsInfinity(y))
            throw new ArgumentException("Y must be finite or NaN (a gap).", nameof(model));

        var index = Count;
        var offset = index & ChunkMask;
        if (offset == 0) _chunks.Add(new Chunk());
        var chunk = _chunks[index >> ChunkShift];
        chunk.Models[offset] = model;
        chunk.X[offset] = x;
        chunk.Y[offset] = y;
        Count++;
        Version++;
        var sample = double.IsNaN(y) ? new Summary(-1, -1, index, 0, 0) : new Summary(index, index, -1, y, y);
        _pending = Merge(_pending, sample);
        _all = Merge(_all, sample);
        if (Count % BlockSize != 0) return;

        var completed = _pending;
        _pending = Summary.Empty;
        for (var level = 0; ; level++)
        {
            if (level == _levels.Count) _levels.Add(new List<Summary>());
            var nodes = _levels[level];
            nodes.Add(completed);
            if ((nodes.Count & 1) != 0) break;
            completed = Merge(nodes[nodes.Count - 2], completed);
        }
    }

    /// <summary>
    /// Appends a batch. If enumeration, mapping or validation fails, earlier samples in the batch
    /// remain appended; the failing sample is not appended.
    /// </summary>
    public void AppendRange(IEnumerable<T> models)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        foreach (var model in models) Append(model);
    }

    /// <summary>Releases all retained models and indexed coordinates.</summary>
    public void Clear()
    {
        _chunks.Clear();
        _levels.Clear();
        _pending = _all = Summary.Empty;
        Count = 0;
        Version++;
        Generation++;
    }

    /// <summary>Returns the original model, index and cached coordinates.</summary>
    public TimeSeriesSample<T> GetSample(int index)
    {
        ValidateIndex(index);
        var chunk = _chunks[index >> ChunkShift];
        var offset = index & ChunkMask;
        return new TimeSeriesSample<T>(index, chunk.Models[offset], chunk.X[offset], chunk.Y[offset]);
    }

    // Renderer-selected indices are already valid. Avoid fetching/copying the original model
    // when only the cached Cartesian coordinates are needed for a visual.
    internal void GetXY(int index, out double x, out double y)
    {
        var chunk = _chunks[index >> ChunkShift];
        var offset = index & ChunkMask;
        x = chunk.X[offset];
        y = chunk.Y[offset];
    }

    /// <summary>
    /// Returns the nearest original sample by X, or -1 when empty. Ties choose the smaller X;
    /// exact duplicate X values choose the first duplicate. NaN gaps are eligible results.
    /// </summary>
    public int FindNearestIndex(double x)
    {
        if (double.IsNaN(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (Count == 0) return -1;
        var upper = LowerBound(x);
        if (upper == 0) return 0;
        if (upper == Count) return Count - 1;
        return x - X(upper - 1) <= X(upper) - x ? upper - 1 : upper;
    }

    /// <summary>Gets full-history X bounds and finite Y extrema; false if there is no finite Y.</summary>
    public bool TryGetBounds(out double minX, out double maxX, out double minY, out double maxY)
    {
        minX = Count == 0 ? double.NaN : X(0);
        maxX = Count == 0 ? double.NaN : X(Count - 1);
        return GetYBounds(_all, out minY, out maxY);
    }

    /// <summary>Gets exact finite Y extrema for samples inside the inclusive X interval.</summary>
    public bool TryGetBounds(double minX, double maxX, out double minY, out double maxY)
    {
        ValidateRange(minX, maxX);
        return GetYBounds(Query(LowerBound(minX), UpperBound(maxX)), out minY, out maxY);
    }

    /// <summary>Reports any NaN Y between two original indices, including both endpoints.</summary>
    public bool HasGapBetween(int firstIndex, int lastIndex)
    {
        ValidateIndex(firstIndex);
        ValidateIndex(lastIndex);
        if (lastIndex < firstIndex) throw new ArgumentOutOfRangeException(nameof(lastIndex));
        if (_all.Gap < 0) return false;
        return Query(firstIndex, lastIndex + 1).Gap >= 0;
    }

    /// <summary>
    /// Replaces destination with chronological original indices: first, minimum, maximum, last
    /// and first gap per equal-width X bucket, plus one neighbor on each side of the viewport.
    /// At most 5 * pixelWidth + 2 indices are returned. Extrema and sample identity are exact;
    /// multiple gaps and oscillations within one pixel are intentionally reduced. A renderer must
    /// use <see cref="HasGapBetween"/> for consecutive selected indices to avoid joining over
    /// an omitted gap. This is a linear-X sampling policy, not a logarithmic-axis policy.
    /// </summary>
    public void Select(double minX, double maxX, int pixelWidth, List<int> destination)
        => SelectFromBucket(minX, maxX, pixelWidth, 0, destination);

    // Uses the original full viewport's bucket boundaries. The preceding raw sample is also
    // the last representative in a populated prefix, allowing the renderer to join a cached
    // prefix without reconnecting across an omitted sample or gap.
    internal void SelectFromBucket(double minX, double maxX, int pixelWidth, int firstBucket, List<int> destination)
    {
        ValidateRange(minX, maxX);
        if (pixelWidth < 1) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (firstBucket < 0 || firstBucket >= pixelWidth) throw new ArgumentOutOfRangeException(nameof(firstBucket));
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        destination.Clear();
        if (Count == 0) return;
        var end = UpperBound(maxX);
        var firstFraction = (double)firstBucket / pixelWidth;
        var startX = firstBucket == 0 ? minX : minX * (1 - firstFraction) + maxX * firstFraction;
        var start = Math.Min(end, LowerBound(startX));
        if (start > 0) destination.Add(start - 1);
        var candidates = new int[5];
        var bucketStart = start;
        for (var pixel = firstBucket; pixel < pixelWidth && bucketStart < end; pixel++)
        {
            var fraction = (pixel + 1.0) / pixelWidth;
            // Weighted interpolation avoids overflowing maxX - minX for finite extremes.
            var boundary = minX * (1 - fraction) + maxX * fraction;
            var bucketEnd = pixel == pixelWidth - 1 ? end : Math.Min(end, LowerBound(boundary));
            if (bucketEnd <= bucketStart) continue;
            var summary = Query(bucketStart, bucketEnd);
            candidates[0] = bucketStart;
            candidates[1] = summary.Min;
            candidates[2] = summary.Max;
            candidates[3] = summary.Gap;
            candidates[4] = bucketEnd - 1;
            Array.Sort(candidates);
            for (var i = 0; i < candidates.Length; i++)
            {
                var index = candidates[i];
                if (index >= 0 && (destination.Count == 0 || destination[destination.Count - 1] != index))
                    destination.Add(index);
            }
            bucketStart = bucketEnd;
        }
        if (end < Count && (destination.Count == 0 || destination[destination.Count - 1] != end))
            destination.Add(end);
    }

    private double X(int index) => _chunks[index >> ChunkShift].X[index & ChunkMask];
    private double Y(int index) => _chunks[index >> ChunkShift].Y[index & ChunkMask];

    private Summary Query(int start, int end)
    {
        var result = Summary.Empty;
        while (start < end && start % BlockSize != 0) result = Include(result, start++);
        var block = start / BlockSize;
        var lastBlock = end / BlockSize;
        while (block < lastBlock)
        {
            var level = 0;
            var size = 1;
            while (level + 1 < _levels.Count && (block & ((size << 1) - 1)) == 0 && (size << 1) <= lastBlock - block)
            {
                level++;
                size *= 2;
            }
            result = Merge(result, _levels[level][block >> level]);
            block += size;
        }
        start = Math.Max(start, block * BlockSize);
        while (start < end) result = Include(result, start++);
        return result;
    }

    private Summary Include(Summary summary, int index)
    {
        var y = Y(index);
        return Merge(summary, double.IsNaN(y) ? new Summary(-1, -1, index, 0, 0) : new Summary(index, index, -1, y, y));
    }

    private Summary Merge(Summary left, Summary right)
    {
        var min = left.Min < 0 ? right : right.Min < 0 ? left : left.MinY <= right.MinY ? left : right;
        var max = left.Max < 0 ? right : right.Max < 0 ? left : left.MaxY >= right.MaxY ? left : right;
        return new Summary(min.Min, max.Max,
            left.Gap < 0 ? right.Gap : right.Gap < 0 ? left.Gap : Math.Min(left.Gap, right.Gap), min.MinY, max.MaxY);
    }

    private bool GetYBounds(Summary summary, out double minY, out double maxY)
    {
        minY = summary.Min < 0 ? double.NaN : Y(summary.Min);
        maxY = summary.Max < 0 ? double.NaN : Y(summary.Max);
        return summary.Min >= 0;
    }

    private int LowerBound(double x)
    {
        var lo = 0;
        var hi = Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (X(mid) < x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private int UpperBound(double x)
    {
        var lo = 0;
        var hi = Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (X(mid) <= x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private void ValidateIndex(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static void ValidateRange(double minX, double maxX)
    {
        if (double.IsNaN(minX) || double.IsInfinity(minX)) throw new ArgumentOutOfRangeException(nameof(minX));
        if (double.IsNaN(maxX) || double.IsInfinity(maxX) || maxX < minX) throw new ArgumentOutOfRangeException(nameof(maxX));
    }

    private sealed class Chunk
    {
        public readonly T[] Models = new T[ChunkSize];
        public readonly double[] X = new double[ChunkSize];
        public readonly double[] Y = new double[ChunkSize];
    }

    private readonly struct Summary(int min, int max, int gap, double minY, double maxY)
    {
        public static Summary Empty => new(-1, -1, -1, 0, 0);
        public int Min { get; } = min;
        public int Max { get; } = max;
        public int Gap { get; } = gap;
        public double MinY { get; } = minY;
        public double MaxY { get; } = maxY;
    }
}
