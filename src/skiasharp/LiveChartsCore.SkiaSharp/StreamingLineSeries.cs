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
using System.Diagnostics;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Drawing;
using LiveChartsCore.Kernel.Providers;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.Motion;
using LiveChartsCore.Painting;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using SkiaSharp;

namespace LiveChartsCore.SkiaSharpView;

/// <summary>
/// Experimental straight-line measurement series backed by an indexed, append-only source.
/// Draws a bounded viewport representation and resolves tooltips to original measurements.
/// Call chart.Update after publishing batches; source access must be serialized with the chart.
/// Fill, markers, smoothing, stacking, point labels and per-sample transitions are not supported.
/// Linear axes and nearest-X tooltips are supported. Values and Mapping are replaced by Source.
/// </summary>
public sealed class StreamingLineSeries<T> : LineSeries<T>, ISeriesRenderOverride
{
    private readonly Dictionary<IChartView, State> _states = new();

    /// <summary>Creates a streaming series over an application-owned source.</summary>
    public StreamingLineSeries(TimeSeriesBuffer<T> source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Fill = null;
        GeometryFill = null;
        GeometryStroke = null;
        GeometrySize = 0;
        LineSmoothness = 0;
    }

    /// <summary>Gets the raw history and its incremental index.</summary>
    public TimeSeriesBuffer<T> Source { get; }

    /// <summary>
    /// Uses a positive one-device-pixel stroke at any display scale. This overrides the
    /// paint's thickness, retaining its color and antialiasing. Fractional physical stroke
    /// widths can be substantially slower in the software rasterizer for dense paths.
    /// </summary>
    public bool UseOnePixelStroke { get; set => SetProperty(ref field, value); }

    /// <summary>
    /// Caches CPU-rendered pixels for stable viewports and redraws only changed tail columns.
    /// Uses additional per-view memory; animation and unsupported paint effects render directly.
    /// </summary>
    public bool UseRasterCache { get; set => SetProperty(ref field, value); }

    internal bool EnablePerformanceDiagnostics;
    internal long FullSelectionCount, PartialSelectionCount, UnchangedSelectionCount;
    internal long SelectionElapsedTicks, PathBuildElapsedTicks;

    /// <summary>Gets the number of representatives selected by the latest measurement.</summary>
    public int DisplayedPointCount { get; private set; }

    bool ISeriesRenderOverride.TryGetBounds(
        ISeries series, Chart chart, ICartesianAxis secondaryAxis, ICartesianAxis primaryAxis,
        out SeriesBounds bounds)
    {
        if (!Source.TryGetBounds(out var minX, out var maxX, out var minY, out var maxY))
        {
            bounds = new SeriesBounds(new DimensionalBounds(true), false);
            return true;
        }

        var visibleMinX = Math.Max(minX, secondaryAxis.MinLimit ?? minX);
        var visibleMaxX = Math.Min(maxX, secondaryAxis.MaxLimit ?? maxX);
        var visibleMinY = minY;
        var visibleMaxY = maxY;
        if (visibleMinX <= visibleMaxX &&
            Source.TryGetBounds(visibleMinX, visibleMaxX, out var low, out var high))
        {
            visibleMinY = low;
            visibleMaxY = high;
        }
        else
        {
            visibleMinX = minX;
            visibleMaxX = maxX;
        }

        bounds = new SeriesBounds(new DimensionalBounds
        {
            SecondaryBounds = MakeBounds(minX, maxX),
            PrimaryBounds = MakeBounds(minY, maxY),
            VisibleSecondaryBounds = MakeBounds(visibleMinX, visibleMaxX),
            VisiblePrimaryBounds = MakeBounds(visibleMinY, visibleMaxY)
        }, false);
        return true;
    }

    bool ISeriesRenderOverride.TryRender(ISeries series, Chart chart)
    {
        var cartesian = (CartesianChartEngine)chart;
        if (!_states.TryGetValue(chart.View, out var state))
        {
            _states.Add(chart.View, state = new State());
            state.Geometry.RefineViewport = () => RefineViewport(state);
        }

        if (state.Paint != Stroke)
        {
            state.Paint?.RemoveGeometryFromPaintTask(chart.Canvas, state.Geometry);
            state.Paint = Stroke;
        }
        if (Stroke is null || Stroke == Paint.Default || !IsVisible)
        {
            state.Geometry.Path.Rewind();
            DisplayedPointCount = 0;
            return true;
        }

        var xAxis = cartesian.GetXAxis(this);
        var yAxis = cartesian.GetYAxis(this);
        var xScale = xAxis.GetNextScaler(cartesian);
        var yScale = yAxis.GetNextScaler(cartesian);
        if (!xScale.IsLinear || !yScale.IsLinear)
            throw new NotSupportedException("StreamingLineSeries currently requires linear Cartesian axes.");

        var location = chart.DrawMarginLocation;
        var size = chart.DrawMarginSize;
        var x0 = xScale.ToChartValues(location.X);
        var x1 = xScale.ToChartValues(location.X + size.Width);
        var minX = Math.Min(x0, x1);
        var maxX = Math.Max(x0, x1);
        var width = Math.Max(1, (int)Math.Ceiling(size.Width));
        state.TargetMinX = minX;
        state.TargetMaxX = maxX;
        if (!chart.Canvas.DisableAnimations && xAxis.IsVisible && state.XScale is not null)
        {
            var actualX = xAxis.GetActualScaler(cartesian);
            var a0 = actualX.ToChartValues(location.X);
            var a1 = actualX.ToChartValues(location.X + size.Width);
            minX = Math.Min(minX, Math.Min(a0, a1));
            maxX = Math.Max(maxX, Math.Max(a0, a1));
        }
        var scaleUnchanged = state.XScale is not null && state.YScale is not null &&
            state.XScale.ToChartValues(location.X) == xScale.ToChartValues(location.X) &&
            state.XScale.ToChartValues(location.X + size.Width) == xScale.ToChartValues(location.X + size.Width) &&
            state.YScale.ToChartValues(location.Y) == yScale.ToChartValues(location.Y) &&
            state.YScale.ToChartValues(location.Y + size.Height) == yScale.ToChartValues(location.Y + size.Height);
        Select(state, minX, maxX, width);
        state.XScale = xScale;
        state.YScale = yScale;
        state.Count = Source.Count;
        state.Generation = Source.Generation;
        BuildPath(state, scaleUnchanged && (state.PrefixUnchanged || !state.SelectionChanged));
        if (state.SelectionChanged || !scaleUnchanged)
            state.Geometry.InvalidatePath(state.PrefixUnchanged && scaleUnchanged && !xAxis.IsInverted,
                xScale.ToPixels(state.DirtyFromValue));
        state.Geometry.Chart = cartesian;
        state.Geometry.XAxis = xAxis;
        state.Geometry.YAxis = yAxis;
        state.Geometry.TargetXScale = xScale;
        state.Geometry.TargetYScale = yScale;
        state.Geometry.NeedsRefinement = minX != state.TargetMinX || maxX != state.TargetMaxX;
        state.Geometry.UseOnePixelStroke = UseOnePixelStroke;
        state.Geometry.UseRasterCache = UseRasterCache;
        Stroke.ZIndex = ZIndex == 0 ? ((ISeries)this).SeriesId : ZIndex;
        Stroke.AddGeometryToPaintTask(chart.Canvas, state.Geometry);
        chart.Canvas.AddDrawableTask(Stroke, PaintStyle.Stroke, CanvasZone.DrawMargin);
        return true;
    }

    IEnumerable<ChartPoint>? ISeriesRenderOverride.TryFindHitPoints(
        ISeries series, Chart chart, LvcPoint pointerPosition, FindingStrategy strategy, FindPointFor findPointFor)
    {
        if (!IsVisible || !_states.TryGetValue(chart.View, out var state) || state.XScale is null ||
            state.Count == 0 || Source.Count == 0 || state.Generation != Source.Generation)
            return Array.Empty<ChartPoint>();
        var location = chart.DrawMarginLocation;
        var size = chart.DrawMarginSize;
        if (pointerPosition.X < location.X || pointerPosition.X > location.X + size.Width ||
            pointerPosition.Y < location.Y || pointerPosition.Y > location.Y + size.Height)
            return Array.Empty<ChartPoint>();
        if (strategy is FindingStrategy.CompareOnlyY or FindingStrategy.CompareOnlyYTakeClosest)
            return Array.Empty<ChartPoint>();

        var xScale = state.Geometry.DrawnXScale ?? state.XScale;
        var yScale = state.Geometry.DrawnYScale ?? state.YScale!;
        if (state.Geometry.DrawnXScale is not null && state.Geometry.DrawnSourceGeneration != Source.Generation)
            return Array.Empty<ChartPoint>();
        var index = Source.FindNearestIndex(xScale.ToChartValues(pointerPosition.X));
        var visibleCount = state.Geometry.DrawnXScale is null ? state.Count : state.Geometry.DrawnSampleCount;
        index = Math.Min(index, Math.Min(Source.Count, visibleCount) - 1);
        if (index < 0) return Array.Empty<ChartPoint>();
        var sample = Source.GetSample(index);
        if (double.IsNaN(sample.Y)) return Array.Empty<ChartPoint>();
        var x = xScale.ToPixels(sample.X);
        var y = yScale.ToPixels(sample.Y);
        if (strategy is not (FindingStrategy.Automatic or FindingStrategy.CompareOnlyX or FindingStrategy.CompareOnlyXTakeClosest)
            && Math.Abs(y - pointerPosition.Y) > 8)
            return Array.Empty<ChartPoint>();

        var entity = new MappedChartEntity
        {
            Coordinate = sample.Coordinate,
            MetaData = new ChartEntityMetaData { EntityIndex = index }
        };
        var point = new ChartPoint(chart.View, this, entity);
        point.Context.DataSource = sample.Model;
        point.Context.HoverArea = new RectangleHoverArea(x - 4, y - 4, 8, 8)
            .CenterXToolTip().CenterYToolTip();
        return new[] { point };
    }

    void ISeriesRenderOverride.OnRemoved(IChartView view, ISeries series) => Release(view);

    /// <inheritdoc />
    public override void RemoveFromUI(Chart chart)
    {
        Release(chart.View);
        base.RemoveFromUI(chart);
    }

    private void Release(IChartView view)
    {
        if (!_states.TryGetValue(view, out var state)) return;
        state.Paint?.RemoveGeometryFromPaintTask(((ICartesianChartView)view).CoreCanvas, state.Geometry);
        state.Geometry.DisposePaths();
        _states.Remove(view);
    }

    private void Select(State state, double minX, double maxX, int width)
    {
        var started = EnablePerformanceDiagnostics ? Stopwatch.GetTimestamp() : 0;
        SelectCore(state, minX, maxX, width);
        if (EnablePerformanceDiagnostics) SelectionElapsedTicks += Stopwatch.GetTimestamp() - started;
    }

    private void SelectCore(State state, double minX, double maxX, int width)
    {
        state.PrefixUnchanged = false;
        state.SelectionChanged = true;
        state.DirtyFromValue = minX;
        state.PixelUpdateStart = 0;
        var span = maxX - minX;
        if (state.XScale is not null && state.Generation == Source.Generation &&
            state.Count > 0 && Source.Count >= state.Count &&
            state.MinX == minX && state.MaxX == maxX && state.Width == width &&
            span > 0 && !double.IsInfinity(span))
        {
            if (Source.Count == state.Count)
            {
                if (EnablePerformanceDiagnostics) UnchangedSelectionCount++;
                state.SelectionChanged = false;
                return;
            }
            var lastX = Source.GetSample(state.Count - 1).X;
            if (lastX > maxX)
            {
                if (EnablePerformanceDiagnostics) UnchangedSelectionCount++;
                state.SelectionChanged = false;
                return;
            }
            var bucket = lastX <= minX ? 0 : Math.Max(0,
                Math.Min(width - 1, (int)((lastX - minX) / span * width)) - 1);
            Source.SelectFromBucket(minX, maxX, width, bucket, state.Suffix);
            var keep = state.Suffix.Count == 0 ? 0 : state.Indices.BinarySearch(state.Suffix[0]);
            if (keep < 0) keep = ~keep;
            state.DirtyFromValue = keep > 0 ? Source.GetSample(state.Indices[keep - 1]).X : minX;
            state.PrefixUnchanged = true;
            state.PixelUpdateStart = Math.Max(0, keep - 1);
            if (EnablePerformanceDiagnostics) PartialSelectionCount++;
            state.Indices.RemoveRange(keep, state.Indices.Count - keep);
            state.Indices.AddRange(state.Suffix);
            return;
        }

        if (EnablePerformanceDiagnostics) FullSelectionCount++;
        Source.Select(minX, maxX, width, state.Indices);
        state.MinX = minX;
        state.MaxX = maxX;
        state.Width = width;
    }

    private void RefineViewport(State state)
    {
        Select(state, state.TargetMinX, state.TargetMaxX, state.Width);
        state.Count = Source.Count;
        state.Generation = Source.Generation;
        BuildPath(state, false);
        state.Geometry.InvalidatePath(false, 0);
    }

    private void BuildPath(State state, bool reusePixels)
    {
        var started = EnablePerformanceDiagnostics ? Stopwatch.GetTimestamp() : 0;
        if (state.Geometry.SourceGeneration != state.Generation)
        {
            state.Geometry.ResetDrawnScalers();
            reusePixels = false;
        }
        state.Geometry.SampleCount = state.Count;
        state.Geometry.SourceGeneration = state.Generation;
        DisplayedPointCount = state.Indices.Count;
        if (reusePixels && !state.SelectionChanged)
        {
            if (EnablePerformanceDiagnostics) PathBuildElapsedTicks += Stopwatch.GetTimestamp() - started;
            return;
        }
        if (state.PixelCoordinates.Length < state.Indices.Count)
        {
            var capacity = Math.Max(state.Indices.Count, state.PixelCoordinates.Length * 2);
            Array.Resize(ref state.PixelCoordinates, capacity);
            Array.Resize(ref state.StartsSegment, capacity);
        }
        var start = reusePixels ? state.PixelUpdateStart : 0;
        var previous = start > 0 && !float.IsNaN(state.PixelCoordinates[start - 1].Y)
            ? state.Indices[start - 1]
            : -1;
        for (var position = start; position < state.Indices.Count; position++)
        {
            var index = state.Indices[position];
            Source.GetXY(index, out var x, out var y);
            if (double.IsNaN(y))
            {
                state.PixelCoordinates[position] = new SKPoint(0, float.NaN);
                state.StartsSegment[position] = true;
                previous = -1;
                continue;
            }
            state.PixelCoordinates[position] = new SKPoint(state.XScale!.ToPixels(x), state.YScale!.ToPixels(y));
            state.StartsSegment[position] = previous < 0 || Source.HasGapBetween(previous, index);
            previous = index;
        }
        state.Geometry.Path.Rewind();
        for (var position = 0; position < state.Indices.Count; position++)
        {
            var point = state.PixelCoordinates[position];
            if (float.IsNaN(point.Y)) continue;
            if (state.StartsSegment[position]) state.Geometry.Path.MoveTo(point);
            else state.Geometry.Path.LineTo(point);
        }
        if (EnablePerformanceDiagnostics) PathBuildElapsedTicks += Stopwatch.GetTimestamp() - started;
    }

    private static Bounds MakeBounds(double min, double max) => new(min, max)
    {
        IsEmpty = false,
        MinDelta = max > min ? Math.Min(1, max - min) : 1
    };

    private sealed class State
    {
        public readonly List<int> Indices = new();
        public readonly List<int> Suffix = new();
        public readonly StreamingLineGeometry Geometry = new();
        public Paint? Paint;
        public Scaler? XScale;
        public Scaler? YScale;
        public int Count;
        public long Generation;
        public double MinX;
        public double MaxX;
        public double TargetMinX;
        public double TargetMaxX;
        public int Width;
        public bool PrefixUnchanged;
        public bool SelectionChanged;
        public double DirtyFromValue;
        public int PixelUpdateStart;
        public SKPoint[] PixelCoordinates = Array.Empty<SKPoint>();
        public bool[] StartsSegment = Array.Empty<bool>();
    }
}
