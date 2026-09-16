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
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using SkiaSharp;

namespace LiveChartsCore.SkiaSharpView.Drawing.Geometries;

/// <summary>A retained software path for the viewport representation of a streaming series.</summary>
internal sealed class StreamingLineGeometry : DrawnGeometry, IDrawnElement<SkiaSharpDrawingContext>
{
    public SKPath Path { get; } = new();
    private readonly SKPath _transformedPath = new();
    private readonly StreamingRasterCache _rasterCache = new();
    private bool _pendingPathChange;
    private bool _allowPrefixReuse;
    private bool _cachePrefixAvailable;
    private float _dirtyFromX;
    private long _pathRevision;

    public bool UseOnePixelStroke { get; set; }
    public bool UseRasterCache { get; set; }
    public CartesianChartEngine Chart { get; set; } = null!;
    public ICartesianAxis XAxis { get; set; } = null!;
    public ICartesianAxis YAxis { get; set; } = null!;
    public Scaler TargetXScale { get; set; } = null!;
    public Scaler TargetYScale { get; set; } = null!;
    public Scaler? DrawnXScale { get; private set; }
    public Scaler? DrawnYScale { get; private set; }
    public int SampleCount { get; set; }
    public long SourceGeneration { get; set; }
    public int DrawnSampleCount { get; private set; }
    public long DrawnSourceGeneration { get; private set; }
    public bool NeedsRefinement { get; set; }
    public Action? RefineViewport { get; set; }

    public override LvcSize Measure() => new(0, 0);

    public void Draw(SkiaSharpDrawingContext context)
    {
        var actualX = Chart.Canvas.DisableAnimations || !XAxis.IsVisible ? TargetXScale : XAxis.GetActualScaler(Chart);
        var actualY = Chart.Canvas.DisableAnimations || !YAxis.IsVisible ? TargetYScale : YAxis.GetActualScaler(Chart);
        var location = Chart.DrawMarginLocation;
        var size = Chart.DrawMarginSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        var x0 = actualX.ToPixels(TargetXScale.ToChartValues(location.X));
        var x1 = actualX.ToPixels(TargetXScale.ToChartValues(location.X + size.Width));
        var y0 = actualY.ToPixels(TargetYScale.ToChartValues(location.Y));
        var y1 = actualY.ToPixels(TargetYScale.ToChartValues(location.Y + size.Height));
        var sx = (x1 - x0) / size.Width;
        var sy = (y1 - y0) / size.Height;
        var moving = Math.Abs(x0 - location.X) > .001f || Math.Abs(x1 - location.X - size.Width) > .001f ||
            Math.Abs(y0 - location.Y) > .001f || Math.Abs(y1 - location.Y - size.Height) > .001f;
        var path = Path;
        if (moving)
        {
            // Transform coordinates, not the canvas: a viewport zoom must not also change
            // stroke width or defeat Skia's positive one-device-pixel raster fast path.
            _transformedPath.Rewind();
            Path.Transform(SKMatrix.CreateScaleTranslation(sx, sy, x0 - sx * location.X, y0 - sy * location.Y), _transformedPath);
            path = _transformedPath;
            IsValid = false;
        }
        else if (NeedsRefinement)
        {
            NeedsRefinement = false;
            RefineViewport?.Invoke();
        }
        DrawnXScale = moving ? actualX : TargetXScale;
        DrawnYScale = moving ? actualY : TargetYScale;
        DrawnSampleCount = SampleCount;
        DrawnSourceGeneration = SourceGeneration;
        var paint = context.ActiveSkiaPaint;
        var thickness = paint.StrokeWidth;
        if (UseOnePixelStroke)
        {
            var matrix = context.Canvas.TotalMatrix;
            var scale = Math.Max(
                Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY),
                Math.Sqrt(matrix.ScaleY * matrix.ScaleY + matrix.SkewX * matrix.SkewX));
            if (scale > 0) paint.StrokeWidth = (float)(1 / scale);
        }
        try
        {
            if (UseRasterCache && !moving)
            {
                _rasterCache.Draw(context.Canvas, path, paint,
                    new SKRect(location.X, location.Y, location.X + size.Width, location.Y + size.Height),
                    _pathRevision, _dirtyFromX, _allowPrefixReuse && _cachePrefixAvailable);
                _pendingPathChange = false;
                _cachePrefixAvailable = _rasterCache.LastDrawUsedCache;
            }
            else
            {
                context.Canvas.DrawPath(path, paint);
                _cachePrefixAvailable = false;
            }
        }
        finally
        {
            paint.StrokeWidth = thickness;
        }
    }

    public void DisposePaths()
    {
        Path.Dispose();
        _transformedPath.Dispose();
        _rasterCache.Dispose();
    }

    public void InvalidatePath(bool allowPrefixReuse, float dirtyFromX)
    {
        _pathRevision++;
        if (!_pendingPathChange)
        {
            _allowPrefixReuse = allowPrefixReuse;
            _dirtyFromX = dirtyFromX;
        }
        else
        {
            _allowPrefixReuse &= allowPrefixReuse;
            _dirtyFromX = Math.Min(_dirtyFromX, dirtyFromX);
        }
        _pendingPathChange = true;
    }

    public void ResetDrawnScalers()
    {
        DrawnXScale = null;
        DrawnYScale = null;
    }
}
