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
using SkiaSharp;

namespace LiveChartsCore.SkiaSharpView.Drawing.Geometries;

/// <summary>
/// CPU raster cache for a series with an unchanged viewport and append-only tail.
/// The owner supplies a path revision and the first X affected by the changed tail,
/// including the preceding line segment. The cache adds stroke and antialias padding.
/// Uses an eight-bit premultiplied software surface; wide-gamut/HDR color fidelity is
/// outside this experimental cache's contract.
/// </summary>
internal sealed class StreamingRasterCache : IDisposable
{
    private SKSurface? _surface;
    private SKImage? _image;
    private SKMatrix _matrix;
    private SKRect _logicalBounds;
    private SKRectI _deviceBounds;
    private PaintState _paint;
    private long _revision = long.MinValue;
    private bool _disposed;

    /// <summary>Whether the previous call composited cached pixels.</summary>
    public bool LastDrawUsedCache { get; private set; }

    /// <summary>
    /// Draws the cached pixels. logicalBounds is the stable viewport rectangle, not
    /// the growing path bounds. A changing matrix, viewport, or paint rebuilds the cache.
    /// Prefix reuse is supported for positive, axis-aligned transforms only.
    /// </summary>
    public void Draw(
        SKCanvas destination, SKPath path, SKPaint paint, SKRect logicalBounds,
        long revision, float dirtyFromX, bool allowPrefixReuse)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StreamingRasterCache));
        LastDrawUsedCache = false;
        var matrix = destination.TotalMatrix;
        // These effects can depend on destination pixels or extend outside an unknown
        // filter kernel. Preserve their original rendering rather than cache incorrectly.
        if (paint.BlendMode != SKBlendMode.SrcOver || paint.PathEffect is not null ||
            paint.ImageFilter is not null || paint.MaskFilter is not null ||
            matrix.Persp0 != 0 || matrix.Persp1 != 0 || matrix.Persp2 != 1)
        {
            ReleasePixels();
            destination.DrawPath(path, paint);
            return;
        }

        var scale = Math.Max(
            Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY),
            Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY));
        var deviceStroke = paint.StrokeWidth == 0 ? 1 : paint.StrokeWidth * scale;
        var padding = (float)(2 + deviceStroke * Math.Max(1, paint.StrokeMiter));
        var pathBounds = matrix.MapRect(path.Bounds);
        var viewportBounds = matrix.MapRect(logicalBounds);
        var clip = destination.DeviceClipBounds;
        // Keep horizontal storage stable as the newest sample advances. Crop vertically:
        // ten narrow signals should not blend ten full-screen transparent images.
        var bounds = new SKRectI(
            Math.Max(clip.Left, (int)Math.Floor(viewportBounds.Left)),
            Math.Max(clip.Top, (int)Math.Floor(pathBounds.Top - padding)),
            Math.Min(clip.Right, (int)Math.Ceiling(viewportBounds.Right)),
            Math.Min(clip.Bottom, (int)Math.Ceiling(pathBounds.Bottom + padding)));
        if (bounds.Width <= 0 || bounds.Height <= 0 || path.IsEmpty)
        {
            ReleasePixels();
            return;
        }

        var state = new PaintState(paint);
        var fullRedraw = _surface is null || !_matrix.Equals(matrix) ||
            !_logicalBounds.Equals(logicalBounds) || !_deviceBounds.Equals(bounds) || !_paint.Equals(state);
        if (_surface is null || !_deviceBounds.Equals(bounds))
        {
            ReleasePixels();
            _surface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height))
                ?? throw new OutOfMemoryException("Unable to allocate the streaming series raster cache.");
        }

        if (fullRedraw || _revision != revision)
        {
            var left = bounds.Left;
            if (!fullRedraw && allowPrefixReuse && matrix.ScaleX > 0 && matrix.ScaleY > 0 &&
                matrix.SkewX == 0 && matrix.SkewY == 0 && !float.IsNaN(dirtyFromX) && !float.IsInfinity(dirtyFromX))
                left = Math.Max(left, (int)Math.Floor(matrix.MapPoint(dirtyFromX, 0).X - padding));

            if (left < bounds.Right)
            {
                _image?.Dispose();
                _image = null;
                var canvas = _surface.Canvas;
                var saved = canvas.Save();
                try
                {
                    canvas.ResetMatrix();
                    canvas.ClipRect(new SKRect(left - bounds.Left, 0, bounds.Width, bounds.Height));
                    canvas.Clear(SKColors.Transparent);
                    var cacheMatrix = matrix;
                    cacheMatrix.TransX -= bounds.Left;
                    cacheMatrix.TransY -= bounds.Top;
                    canvas.SetMatrix(cacheMatrix);
                    canvas.DrawPath(path, paint);
                }
                finally
                {
                    canvas.RestoreToCount(saved);
                }
            }

            _matrix = matrix;
            _logicalBounds = logicalBounds;
            _deviceBounds = bounds;
            _paint = state;
            _revision = revision;
        }

        _image ??= _surface.Snapshot();
        var destinationSaved = destination.Save();
        try
        {
            destination.ResetMatrix();
            destination.DrawImage(_image, bounds.Left, bounds.Top);
            LastDrawUsedCache = true;
        }
        finally
        {
            destination.RestoreToCount(destinationSaved);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleasePixels();
        _disposed = true;
    }

    private void ReleasePixels()
    {
        _image?.Dispose();
        _image = null;
        _surface?.Dispose();
        _surface = null;
        _paint = default;
    }

    private readonly struct PaintState
    {
        private readonly SKColorF _color;
        private readonly float _width;
        private readonly float _miter;
        private readonly bool _antialias;
        private readonly bool _dither;
        private readonly SKStrokeCap _cap;
        private readonly SKStrokeJoin _join;
        private readonly SKPaintStyle _style;
        private readonly SKBlendMode _blend;
        private readonly SKShader? _shader;
        private readonly SKColorFilter? _colorFilter;

        public PaintState(SKPaint paint)
        {
            _color = paint.ColorF;
            _width = paint.StrokeWidth;
            _miter = paint.StrokeMiter;
            _antialias = paint.IsAntialias;
            _dither = paint.IsDither;
            _cap = paint.StrokeCap;
            _join = paint.StrokeJoin;
            _style = paint.Style;
            _blend = paint.BlendMode;
            _shader = paint.Shader;
            _colorFilter = paint.ColorFilter;
        }

        public bool Equals(PaintState other) =>
            _color.Equals(other._color) && _width == other._width && _miter == other._miter &&
            _antialias == other._antialias && _dither == other._dither && _cap == other._cap &&
            _join == other._join && _style == other._style && _blend == other._blend &&
            ReferenceEquals(_shader, other._shader) && ReferenceEquals(_colorFilter, other._colorFilter);
    }
}
