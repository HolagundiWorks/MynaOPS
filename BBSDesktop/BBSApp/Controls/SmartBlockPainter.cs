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

    /// <summary>The AutoCAD colour of a primitive, as drawn on a dark or light page: the few indices the blocks use, otherwise the outline brush.</summary>
    private static Brush Colour(int? aci, Ink ink) => aci switch
    {
        1 => new SolidColorBrush(Color.FromArgb(255, 230, 40, 40)),
        3 => new SolidColorBrush(Color.FromArgb(255, 40, 190, 60)),
        4 => new SolidColorBrush(Color.FromArgb(255, 20, 180, 190)),
        5 => new SolidColorBrush(Color.FromArgb(255, 70, 100, 245)),
        6 => new SolidColorBrush(Color.FromArgb(255, 200, 50, 200)),
        30 => new SolidColorBrush(Color.FromArgb(255, 235, 130, 20)),
        _ => ink.Outline,
    };

    /// <summary>Paint <paramref name="g"/> into <paramref name="canvas"/> (cleared first) and return the pixels per millimetre.</summary>
    public static double Paint(Canvas canvas, SmartGeometry g, Ink ink)
    {
        canvas.Children.Clear();
        var (w, h) = (canvas.Width, canvas.Height);
        const double margin = 10;
        var b = g.Bounds(withText: true) ?? (-100, -100, 100, 100);
        var bw = Math.Max(b.MaxX - b.MinX, 1);
        var bh = Math.Max(b.MaxY - b.MinY, 1);
        var scale = Math.Min((w - 2 * margin) / bw, (h - 2 * margin) / bh);
        var ox = (w - bw * scale) / 2 - b.MinX * scale;
        var oy = (h - bh * scale) / 2 + b.MaxY * scale; // y is flipped
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
            var stroke = Colour(prim.Color, ink);
            switch (prim)
            {
                case LinePrim l:
                    canvas.Children.Add(new Line { X1 = X(l.X1), Y1 = Y(l.Y1), X2 = X(l.X2), Y2 = Y(l.Y2), Stroke = stroke, StrokeThickness = prim.Color == 30 ? 0.8 : 1.2 });
                    break;
                case PolyPrim p:
                {
                    Shape s = p.Closed ? new Polygon() : new Polyline();
                    var pts = p.Closed ? ((Polygon)s).Points : ((Polyline)s).Points;
                    foreach (var (x, y) in p.Pts)
                        pts.Add(new Windows.Foundation.Point(X(x), Y(y)));
                    s.Stroke = stroke;
                    s.StrokeThickness = prim.Color == 30 ? 0.8 : 1.2;
                    s.StrokeLineJoin = PenLineJoin.Round;
                    canvas.Children.Add(s);
                    break;
                }
                case CirclePrim c:
                    Ellipse(canvas, X(c.X), Y(c.Y), c.R * scale, stroke, null, 1.2);
                    break;
                case DotPrim d:
                    Ellipse(canvas, X(d.X), Y(d.Y), Math.Max(d.R * scale, 1.6), null, prim.Color is null ? ink.Bar(d.R * 2) : stroke, 0);
                    break;
                case ArcPrim a:
                    canvas.Children.Add(Arc(a, X, Y, scale, stroke));
                    break;
                case TextPrim t:
                    Text(canvas, t, X, Y, scale, prim.Color is null ? ink.Text : stroke);
                    break;
            }
        }
        return scale;
    }

    /// <summary>A text at its anchor, at the height it has in the drawing (but not below 7 px, so it can be read), turned for a vertical dimension.</summary>
    private static void Text(Canvas canvas, TextPrim t, Func<double, double> x, Func<double, double> y, double scale, Brush brush)
    {
        var size = Math.Max(t.Height * scale, 7);
        var tb = new TextBlock { Text = t.Value, FontSize = size, Foreground = brush, TextWrapping = TextWrapping.NoWrap };
        tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var (tw, th) = (tb.DesiredSize.Width, tb.DesiredSize.Height);
        var (hz, vt) = t.Centred ? ("c", "m") : (t.H, t.V);
        // The anchor inside the text's own box, from its top left.
        var ax = hz switch { "c" => tw / 2, "r" => tw, _ => 0 };
        var ay = vt switch { "m" => th / 2, "t" => 0, _ => th * 0.8 };
        tb.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        var turned = Math.Abs(t.RotationDeg - 90) < 1e-6;
        // A turned text reads from bottom to top: its box is rotated a quarter turn counter-clockwise about the anchor.
        tb.RenderTransform = turned ? new RotateTransform { Angle = -90, CenterX = ax, CenterY = ay } : null;
        Canvas.SetLeft(tb, x(t.X) - ax);
        Canvas.SetTop(tb, y(t.Y) - ay);
        canvas.Children.Add(tb);
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
