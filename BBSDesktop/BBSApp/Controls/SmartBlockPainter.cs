// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Office.Shared.SmartBlocks;
using Windows.UI;

namespace BBSApp.Controls;

/// <summary>Draws an evaluated smart block (millimetres, y up) onto a canvas, fitted to its size.</summary>
public static class SmartBlockPainter
{
    /// <summary>The brushes a detail is drawn with, taken from the theme by the caller.</summary>
    public sealed record Ink(Brush Outline, Brush Text, Brush Hatch, Func<double, Brush> Bar);

    /// <summary>Paint <paramref name="g"/> into <paramref name="canvas"/> (cleared first) and return the pixels per millimetre.</summary>
    public static double Paint(Canvas canvas, SmartGeometry g, Ink ink)
    {
        canvas.Children.Clear();
        var (w, h) = (canvas.Width, canvas.Height);
        const double margin = 14;
        // The labels go in a band under the drawing, one under another, so they never cover it at any scale.
        var labels = g.Prims.OfType<TextPrim>().Select(t => new TextBlock
        {
            Text = t.Value,
            FontSize = 11,
            Foreground = ink.Text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Width = w - 2 * 6,
        }).ToList();
        foreach (var tb in labels)
            tb.Measure(new Windows.Foundation.Size(w, h));
        var band = labels.Sum(tb => tb.DesiredSize.Height + 4);
        var b = g.Bounds() ?? (-100, -100, 100, 100);
        var bw = Math.Max(b.MaxX - b.MinX, 1);
        var bh = Math.Max(b.MaxY - b.MinY, 1);
        var scale = Math.Min((w - 2 * margin) / bw, (h - 2 * margin - band) / bh);
        var ox = (w - bw * scale) / 2 - b.MinX * scale;
        var top = margin + Math.Max(0, (h - 2 * margin - band - bh * scale) / 2);
        var oy = top + b.MaxY * scale; // y is flipped
        double X(double x) => ox + x * scale;
        double Y(double y) => oy - y * scale;

        foreach (var prim in g.Prims.OfType<HatchPrim>())
        {
            var hatch = new Polygon { Fill = ink.Hatch, Opacity = 0.35 };
            foreach (var (x, y) in prim.Pts)
                hatch.Points.Add(new Windows.Foundation.Point(X(x), Y(y)));
            canvas.Children.Add(hatch);
        }
        foreach (var prim in g.Prims)
        {
            switch (prim)
            {
                case LinePrim l:
                    canvas.Children.Add(new Line { X1 = X(l.X1), Y1 = Y(l.Y1), X2 = X(l.X2), Y2 = Y(l.Y2), Stroke = ink.Outline, StrokeThickness = 1.2 });
                    break;
                case PolyPrim p:
                {
                    Shape s = p.Closed ? new Polygon() : new Polyline();
                    var pts = p.Closed ? ((Polygon)s).Points : ((Polyline)s).Points;
                    foreach (var (x, y) in p.Pts)
                        pts.Add(new Windows.Foundation.Point(X(x), Y(y)));
                    s.Stroke = ink.Outline;
                    s.StrokeThickness = 1.2;
                    s.StrokeLineJoin = PenLineJoin.Round;
                    canvas.Children.Add(s);
                    break;
                }
                case CirclePrim c:
                    Ellipse(canvas, X(c.X), Y(c.Y), c.R * scale, ink.Outline, null, 1.2);
                    break;
                case DotPrim d:
                    Ellipse(canvas, X(d.X), Y(d.Y), Math.Max(d.R * scale, 1.6), null, ink.Bar(d.R * 2), 0);
                    break;
                case ArcPrim a:
                    canvas.Children.Add(Arc(a, X, Y, scale, ink.Outline));
                    break;
            }
        }
        var stack = new StackPanel { Spacing = 4, Width = w - 2 * 6 };
        foreach (var tb in labels)
            stack.Children.Add(tb);
        Canvas.SetLeft(stack, 6);
        Canvas.SetTop(stack, top + bh * scale + 8);
        canvas.Children.Add(stack);
        return scale;
    }

    private static void Ellipse(Canvas canvas, double cx, double cy, double r, Brush? stroke, Brush? fill, double thickness)
    {
        var e = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = stroke, Fill = fill, StrokeThickness = thickness };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        canvas.Children.Add(e);
    }

    private static Microsoft.UI.Xaml.Shapes.Path Arc(ArcPrim a, Func<double, double> x, Func<double, double> y, double scale, Brush stroke)
    {
        double Rad(double d) => d * Math.PI / 180;
        var (s, e) = (Rad(a.StartDeg), Rad(a.EndDeg));
        var sweep = e - s;
        while (sweep <= 0)
            sweep += 2 * Math.PI;
        var start = new Windows.Foundation.Point(x(a.X + a.R * Math.Cos(s)), y(a.Y + a.R * Math.Sin(s)));
        var end = new Windows.Foundation.Point(x(a.X + a.R * Math.Cos(e)), y(a.Y + a.R * Math.Sin(e)));
        var seg = new ArcSegment
        {
            Point = end,
            Size = new Windows.Foundation.Size(a.R * scale, a.R * scale),
            SweepDirection = SweepDirection.Counterclockwise, // y is flipped, so a counter-clockwise arc stays so on screen
            IsLargeArc = sweep > Math.PI,
        };
        var fig = new PathFigure { StartPoint = start, Segments = { seg } };
        return new Microsoft.UI.Xaml.Shapes.Path { Data = new PathGeometry { Figures = { fig } }, Stroke = stroke, StrokeThickness = 1.2 };
    }
}
