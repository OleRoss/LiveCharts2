using System;
using System.Collections.Generic;
using System.Linq;
using LiveChartsCore.Defaults;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreTests;

[TestClass]
public class TimeSeriesBufferTests
{
    [TestMethod]
    public void SelectionMatchesBruteForceBucketsAcrossAppendAndChunkBoundaries()
    {
        var random = new Random(401);
        var models = new List<Measurement>();
        var buffer = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
        var selected = new List<int>();
        foreach (var size in new[] { 1, 7, 8, 9, 31, 32, 33, 1023, 4095, 4096, 4097, 13001 })
        {
            while (models.Count < size)
            {
                var model = new Measurement(models.Count / 3, random.NextDouble() < .07 ? double.NaN : random.Next(-100, 101));
                models.Add(model);
                buffer.Append(model);
            }
            for (var trial = 0; trial < 12; trial++)
            {
                var left = random.Next(-3, size / 3 + 3);
                var right = left + random.Next(0, size / 3 + 1);
                var pixels = random.Next(1, 33);
                buffer.Select(left, right, pixels, selected);
                CollectionAssert.AreEqual(BruteForce(models, left, right, pixels), selected);
                Assert.IsTrue(selected.Count <= pixels * 5 + 2);
                var visible = models.Where(m => m.X >= left && m.X <= right && !double.IsNaN(m.Y)).ToList();
                Assert.AreEqual(visible.Count != 0, buffer.TryGetBounds(left, right, out var min, out var max));
                if (visible.Count != 0)
                {
                    Assert.AreEqual(visible.Min(m => m.Y), min);
                    Assert.AreEqual(visible.Max(m => m.Y), max);
                }
            }
        }
    }

    [TestMethod]
    public void SuffixSelectionRejoinsTheExactFullSelectionAcrossAppendsAndGaps()
    {
        var source = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
        var random = new Random(691);
        var full = new List<int>();
        var suffix = new List<int>();
        foreach (var count in new[] { 7, 32, 4097, 8001 })
        {
            while (source.Count < count)
                source.Append(new Measurement(source.Count / 3, source.Count % 17 == 0 ? double.NaN : random.Next(-100, 100)));
            for (var trial = 0; trial < 20; trial++)
            {
                var min = random.Next(-10, count / 3 + 10);
                var max = min + random.Next(0, count / 3 + 1);
                var width = random.Next(1, 25);
                source.Select(min, max, width, full);
                for (var firstBucket = 0; firstBucket < width; firstBucket++)
                {
                    source.SelectFromBucket(min, max, width, firstBucket, suffix);
                    var joined = suffix.Count == 0 ? full : full.Where(index => index < suffix[0]).Concat(suffix).ToList();
                    CollectionAssert.AreEqual(full, joined);
                    Assert.IsTrue(suffix.Count <= 5 * (width - firstBucket) + 2);
                }
            }
        }
    }

    [TestMethod]
    public void MappingRunsOnceAndSelectedSamplesRetainIdentity()
    {
        var xCalls = 0;
        var yCalls = 0;
        var buffer = new TimeSeriesBuffer<Measurement>(m => { xCalls++; return m.X; }, m => { yCalls++; return m.Y; });
        var model = new Measurement(4, 7);
        buffer.Append(model);
        model.X = 20;
        model.Y = 30;
        var selected = new List<int>();
        buffer.Select(0, 10, 100, selected);
        var sample = buffer.GetSample(selected[0]);
        Assert.AreSame(model, sample.Model);
        Assert.AreEqual(0, sample.Index);
        Assert.AreEqual(4d, sample.Coordinate.SecondaryValue);
        Assert.AreEqual(7d, sample.Coordinate.PrimaryValue);
        buffer.GetXY(0, out var cachedX, out var cachedY);
        Assert.AreEqual(4d, cachedX);
        Assert.AreEqual(7d, cachedY);
        Assert.AreEqual(1, xCalls);
        Assert.AreEqual(1, yCalls);
    }

    [TestMethod]
    public void GapQueryFindsOmittedGapsAndResetDropsOldExtrema()
    {
        var buffer = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
        buffer.AppendRange(Enumerable.Range(0, 10000).Select(i => new Measurement(i, i % 137 == 0 ? double.NaN : i)));
        var selected = new List<int>();
        buffer.Select(0, 9999, 1, selected);
        Assert.IsTrue(selected.Count <= 5);
        Assert.IsTrue(buffer.HasGapBetween(1, 9998));
        Assert.IsFalse(buffer.HasGapBetween(1, 136));
        Assert.IsTrue(buffer.HasGapBetween(137, 137));
        Assert.IsTrue(buffer.GetSample(0).Coordinate.IsEmpty);
        var version = buffer.Version;
        buffer.Clear();
        Assert.IsTrue(buffer.Version > version);
        Assert.AreEqual(-1, buffer.FindNearestIndex(1));
        Assert.IsFalse(buffer.TryGetBounds(out _, out _, out _, out _));
        buffer.Append(new Measurement(2, -5));
        Assert.IsTrue(buffer.TryGetBounds(out var minX, out var maxX, out var minY, out var maxY));
        Assert.AreEqual(2d, minX);
        Assert.AreEqual(2d, maxX);
        Assert.AreEqual(-5d, minY);
        Assert.AreEqual(-5d, maxY);
    }

    [TestMethod]
    public void NearestSampleHandlesDuplicatesTiesAndOutsideQueries()
    {
        var buffer = new TimeSeriesBuffer<double>(x => x, x => x);
        buffer.AppendRange(new[] { 1d, 3d, 3d, 5d });
        Assert.AreEqual(0, buffer.FindNearestIndex(-100));
        Assert.AreEqual(3, buffer.FindNearestIndex(100));
        Assert.AreEqual(1, buffer.FindNearestIndex(3));
        Assert.AreEqual(0, buffer.FindNearestIndex(2));
        Assert.AreEqual(2, buffer.FindNearestIndex(4));
        var selected = new List<int>();
        buffer.Select(3, 3, 4, selected);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, selected);
    }

    [TestMethod]
    public void AllGapViewportAndExtremeFiniteXHaveBoundedOrderedSelection()
    {
        var buffer = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
        for (var i = 0; i < 100; i++) buffer.Append(new Measurement(i, double.NaN));
        Assert.IsFalse(buffer.TryGetBounds(10, 90, out var min, out var max));
        Assert.IsTrue(double.IsNaN(min));
        Assert.IsTrue(double.IsNaN(max));
        var selected = new List<int>();
        buffer.Select(10, 90, 3, selected);
        Assert.AreEqual(9, selected[0]);
        Assert.AreEqual(91, selected[selected.Count - 1]);
        Assert.IsTrue(selected.Count <= 17);
        Assert.IsTrue(selected.All(i => buffer.GetSample(i).Coordinate.IsEmpty));

        buffer.Clear();
        buffer.AppendRange(new[]
        {
            new Measurement(-double.MaxValue, 1),
            new Measurement(0, 2),
            new Measurement(double.MaxValue, 3)
        });
        buffer.Select(-double.MaxValue, double.MaxValue, 3, selected);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, selected);
    }

    [TestMethod]
    public void InvalidSamplesCannotCorruptPreviouslyAppendedIndex()
    {
        var buffer = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
        buffer.Append(new Measurement(1, 5));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Append(new Measurement(0, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Append(new Measurement(double.NaN, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Append(new Measurement(double.PositiveInfinity, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Append(new Measurement(2, double.PositiveInfinity)));
        Assert.AreEqual(1, buffer.Count);
        Assert.IsTrue(buffer.TryGetBounds(out _, out _, out var min, out var max));
        Assert.AreEqual(5d, min);
        Assert.AreEqual(5d, max);
    }

    private static List<int> BruteForce(List<Measurement> models, double minX, double maxX, int pixels)
    {
        var result = new List<int>();
        var start = models.FindIndex(m => m.X >= minX);
        if (start < 0) start = models.Count;
        var end = models.FindIndex(m => m.X > maxX);
        if (end < 0) end = models.Count;
        if (start > 0) result.Add(start - 1);
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var fraction = (pixel + 1d) / pixels;
            var boundary = minX * (1 - fraction) + maxX * fraction;
            var indices = Enumerable.Range(start, end - start).TakeWhile(i => pixel == pixels - 1 || models[i].X < boundary).ToList();
            if (indices.Count == 0) continue;
            var finite = indices.Where(i => !double.IsNaN(models[i].Y)).ToList();
            var candidates = new List<int> { indices[0], indices[indices.Count - 1] };
            if (finite.Count != 0)
            {
                candidates.Add(finite.OrderBy(i => models[i].Y).First());
                candidates.Add(finite.OrderByDescending(i => models[i].Y).First());
            }
            var gaps = indices.Where(i => double.IsNaN(models[i].Y)).ToList();
            if (gaps.Count != 0) candidates.Add(gaps[0]);
            result.AddRange(candidates.Distinct().OrderBy(i => i));
            start = indices[indices.Count - 1] + 1;
        }
        if (end < models.Count && (result.Count == 0 || result[result.Count - 1] != end)) result.Add(end);
        return result;
    }

    private sealed class Measurement(double x, double y)
    {
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
    }
}
