using System.Globalization;
using MiniRec.Services;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace MiniRec.Models;

internal enum AnnotationToolType
{
    None,
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Highlighter,
    NumberBadge,
    Mosaic,
    Text,
    Emoji
}

internal enum MosaicMode
{
    Pixel,
    Blur
}

internal enum AnnotationSizeLevel
{
    Small,
    Medium,
    Large
}

internal sealed record AnnotationStyle(
    Media.Color Color,
    AnnotationSizeLevel SizeLevel = AnnotationSizeLevel.Medium,
    MosaicMode MosaicMode = MosaicMode.Pixel)
{
    public double StrokeWidth => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 2.5,
        AnnotationSizeLevel.Large => 7.5,
        _ => 4.5
    };

    public double HighlighterWidth => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 14.0,
        AnnotationSizeLevel.Large => 32.0,
        _ => 22.0
    };

    public double FontSize => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 16.0,
        AnnotationSizeLevel.Large => 30.0,
        _ => 22.0
    };

    public double BadgeRadius => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 12.0,
        AnnotationSizeLevel.Large => 19.0,
        _ => 15.0
    };

    public double MosaicBrushWidth => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 18.0,
        AnnotationSizeLevel.Large => 40.0,
        _ => 28.0
    };

    public int MosaicBlockSize => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 8,
        AnnotationSizeLevel.Large => 22,
        _ => 14
    };

    public int MosaicBlurRadius => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 5,
        AnnotationSizeLevel.Large => 16,
        _ => 10
    };

    public double EmojiSize => SizeLevel switch
    {
        AnnotationSizeLevel.Small => 36.0,
        AnnotationSizeLevel.Large => 76.0,
        _ => 54.0
    };
}

internal delegate (Imaging.BitmapSource? Bitmap, CaptureRegion CacheRegion) MosaicSourceProvider(
    MosaicMode mode,
    int strength);

internal abstract class AnnotationItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public AnnotationStyle Style { get; set; }

    protected AnnotationItem(AnnotationStyle style)
    {
        Style = style;
    }

    public abstract Wpf.Rect GetPhysicalBounds();
    public abstract bool HitTest(Wpf.Point physPt, double tolerance);
    public abstract void Translate(double dx, double dy);
    public abstract void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider);

    public virtual void DrawSelectionOutline(Media.DrawingContext dc, double invScale)
    {
        Wpf.Rect b = GetPhysicalBounds();
        if (b.Width <= 0 && b.Height <= 0) return;

        double pad = 5.0 * invScale;
        var rect = new Wpf.Rect(b.X - pad, b.Y - pad, b.Width + pad * 2, b.Height + pad * 2);

        var bgPen = new Media.Pen(Media.Brushes.White, 1.5 * invScale);
        bgPen.Freeze();
        dc.DrawRectangle(null, bgPen, rect);

        var dashPen = new Media.Pen(
            new Media.SolidColorBrush(Media.Color.FromRgb(7, 193, 96)),
            1.5 * invScale)
        {
            DashStyle = Media.DashStyles.Dash
        };
        dashPen.Freeze();
        dc.DrawRectangle(null, dashPen, rect);
    }

    protected static double Distance(Wpf.Point a, Wpf.Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    protected static double DistanceToSegment(Wpf.Point p, Wpf.Point a, Wpf.Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6)
        {
            return Distance(p, a);
        }

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
        var proj = new Wpf.Point(a.X + t * dx, a.Y + t * dy);
        return Distance(p, proj);
    }
}

internal sealed class RectAnnotation : AnnotationItem
{
    public Wpf.Point Start { get; set; }
    public Wpf.Point End { get; set; }

    public RectAnnotation(AnnotationStyle style, Wpf.Point start, Wpf.Point end) : base(style)
    {
        Start = start;
        End = end;
    }

    public override Wpf.Rect GetPhysicalBounds() =>
        new(
            Math.Min(Start.X, End.X),
            Math.Min(Start.Y, End.Y),
            Math.Abs(End.X - Start.X),
            Math.Abs(End.Y - Start.Y));

    public override bool HitTest(Wpf.Point physPt, double tolerance)
    {
        Wpf.Rect r = GetPhysicalBounds();
        double tol = Math.Max(tolerance, Style.StrokeWidth * 0.75 + 4);
        if (physPt.X < r.Left - tol || physPt.X > r.Right + tol ||
            physPt.Y < r.Top - tol || physPt.Y > r.Bottom + tol)
        {
            return false;
        }

        bool nearH = Math.Abs(physPt.Y - r.Top) <= tol || Math.Abs(physPt.Y - r.Bottom) <= tol;
        bool nearV = Math.Abs(physPt.X - r.Left) <= tol || Math.Abs(physPt.X - r.Right) <= tol;
        return nearH || nearV;
    }

    public override void Translate(double dx, double dy)
    {
        Start = new Wpf.Point(Start.X + dx, Start.Y + dy);
        End = new Wpf.Point(End.X + dx, End.Y + dy);
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        Wpf.Rect r = GetPhysicalBounds();
        if (r.Width < 1 && r.Height < 1) return;

        var brush = new Media.SolidColorBrush(Style.Color);
        brush.Freeze();
        var pen = new Media.Pen(brush, Style.StrokeWidth)
        {
            LineJoin = Media.PenLineJoin.Round
        };
        pen.Freeze();
        double corner = Math.Min(3.0, Math.Min(r.Width, r.Height) * 0.15);
        dc.DrawRoundedRectangle(null, pen, r, corner, corner);
    }
}

internal sealed class EllipseAnnotation : AnnotationItem
{
    public Wpf.Point Start { get; set; }
    public Wpf.Point End { get; set; }

    public EllipseAnnotation(AnnotationStyle style, Wpf.Point start, Wpf.Point end) : base(style)
    {
        Start = start;
        End = end;
    }

    public override Wpf.Rect GetPhysicalBounds() =>
        new(
            Math.Min(Start.X, End.X),
            Math.Min(Start.Y, End.Y),
            Math.Abs(End.X - Start.X),
            Math.Abs(End.Y - Start.Y));

    public override bool HitTest(Wpf.Point physPt, double tolerance)
    {
        Wpf.Rect r = GetPhysicalBounds();
        double rx = r.Width / 2.0;
        double ry = r.Height / 2.0;
        if (rx < 1 || ry < 1) return false;

        double cx = r.Left + rx;
        double cy = r.Top + ry;
        double tol = Math.Max(tolerance, Style.StrokeWidth * 0.75 + 4);

        double nxOuter = (physPt.X - cx) / (rx + tol);
        double nyOuter = (physPt.Y - cy) / (ry + tol);
        if (nxOuter * nxOuter + nyOuter * nyOuter > 1.0) return false;

        double innerRx = Math.Max(1.0, rx - tol);
        double innerRy = Math.Max(1.0, ry - tol);
        double nxInner = (physPt.X - cx) / innerRx;
        double nyInner = (physPt.Y - cy) / innerRy;
        return rx <= tol || ry <= tol || (nxInner * nxInner + nyInner * nyInner >= 1.0);
    }

    public override void Translate(double dx, double dy)
    {
        Start = new Wpf.Point(Start.X + dx, Start.Y + dy);
        End = new Wpf.Point(End.X + dx, End.Y + dy);
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        Wpf.Rect r = GetPhysicalBounds();
        if (r.Width < 1 && r.Height < 1) return;

        var brush = new Media.SolidColorBrush(Style.Color);
        brush.Freeze();
        var pen = new Media.Pen(brush, Style.StrokeWidth);
        pen.Freeze();
        dc.DrawEllipse(null, pen, new Wpf.Point(r.X + r.Width / 2.0, r.Y + r.Height / 2.0), r.Width / 2.0, r.Height / 2.0);
    }
}

internal sealed class ArrowAnnotation : AnnotationItem
{
    public Wpf.Point Start { get; set; }
    public Wpf.Point End { get; set; }

    public ArrowAnnotation(AnnotationStyle style, Wpf.Point start, Wpf.Point end) : base(style)
    {
        Start = start;
        End = end;
    }

    public override Wpf.Rect GetPhysicalBounds()
    {
        double pad = Style.StrokeWidth * 2.5 + 8;
        return new Wpf.Rect(
            Math.Min(Start.X, End.X) - pad,
            Math.Min(Start.Y, End.Y) - pad,
            Math.Abs(End.X - Start.X) + pad * 2,
            Math.Abs(End.Y - Start.Y) + pad * 2);
    }

    public override bool HitTest(Wpf.Point physPt, double tolerance) =>
        DistanceToSegment(physPt, Start, End) <= Math.Max(tolerance, Style.StrokeWidth + 6);

    public override void Translate(double dx, double dy)
    {
        Start = new Wpf.Point(Start.X + dx, Start.Y + dy);
        End = new Wpf.Point(End.X + dx, End.Y + dy);
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        double dx = End.X - Start.X;
        double dy = End.Y - Start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 3.0) return;

        double ux = dx / len;
        double uy = dy / len;
        double nx = -uy;
        double ny = ux;

        double sw = Style.StrokeWidth;
        double headLen = Math.Clamp(len * 0.36, sw * 2.8, sw * 4.2 + 12);
        double headHalfW = headLen * 0.45;
        double neckHalfW = Math.Max(1.3, sw * 0.65);
        double tailHalfW = Math.Max(0.7, sw * 0.25);

        double neckDist = Math.Max(1.0, len - headLen);
        var neckCenter = new Wpf.Point(Start.X + ux * neckDist, Start.Y + uy * neckDist);
        double headBaseDist = Math.Max(1.0, len - headLen * 0.85);
        var headBaseCenter = new Wpf.Point(Start.X + ux * headBaseDist, Start.Y + uy * headBaseDist);

        var geo = new Media.StreamGeometry();
        using (Media.StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(new Wpf.Point(Start.X + nx * tailHalfW, Start.Y + ny * tailHalfW), isFilled: true, isClosed: true);
            ctx.LineTo(new Wpf.Point(neckCenter.X + nx * neckHalfW, neckCenter.Y + ny * neckHalfW), true, false);
            ctx.LineTo(new Wpf.Point(headBaseCenter.X + nx * headHalfW, headBaseCenter.Y + ny * headHalfW), true, false);
            ctx.LineTo(End, true, false);
            ctx.LineTo(new Wpf.Point(headBaseCenter.X - nx * headHalfW, headBaseCenter.Y - ny * headHalfW), true, false);
            ctx.LineTo(new Wpf.Point(neckCenter.X - nx * neckHalfW, neckCenter.Y - ny * neckHalfW), true, false);
            ctx.LineTo(new Wpf.Point(Start.X - nx * tailHalfW, Start.Y - ny * tailHalfW), true, false);
        }
        geo.Freeze();

        var brush = new Media.SolidColorBrush(Style.Color);
        brush.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }
}

internal sealed class StrokeAnnotation : AnnotationItem
{
    public List<Wpf.Point> Points { get; }
    public bool IsHighlighter { get; }

    public StrokeAnnotation(AnnotationStyle style, bool isHighlighter, IEnumerable<Wpf.Point> points) : base(style)
    {
        IsHighlighter = isHighlighter;
        Points = new List<Wpf.Point>(points);
    }

    public override Wpf.Rect GetPhysicalBounds()
    {
        if (Points.Count == 0) return Wpf.Rect.Empty;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (Wpf.Point p in Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        double pad = (IsHighlighter ? Style.HighlighterWidth : Style.StrokeWidth) * 0.5;
        return new Wpf.Rect(minX - pad, minY - pad, (maxX - minX) + pad * 2, (maxY - minY) + pad * 2);
    }

    public override bool HitTest(Wpf.Point physPt, double tolerance)
    {
        if (Points.Count == 0) return false;
        double halfW = (IsHighlighter ? Style.HighlighterWidth : Style.StrokeWidth) * 0.5;
        double tol = Math.Max(tolerance, halfW + 4);
        if (Points.Count == 1) return Distance(physPt, Points[0]) <= tol;

        for (int i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(physPt, Points[i - 1], Points[i]) <= tol)
            {
                return true;
            }
        }
        return false;
    }

    public override void Translate(double dx, double dy)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new Wpf.Point(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        if (Points.Count == 0) return;

        Media.Color c = IsHighlighter
            ? Media.Color.FromArgb(115, Style.Color.R, Style.Color.G, Style.Color.B)
            : Style.Color;
        var brush = new Media.SolidColorBrush(c);
        brush.Freeze();

        double width = IsHighlighter ? Style.HighlighterWidth : Style.StrokeWidth;
        if (Points.Count == 1)
        {
            dc.DrawEllipse(brush, null, Points[0], width / 2.0, width / 2.0);
            return;
        }

        var geo = new Media.StreamGeometry();
        using (Media.StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(Points[0], isFilled: false, isClosed: false);
            for (int i = 1; i < Points.Count; i++)
            {
                ctx.LineTo(Points[i], true, true);
            }
        }
        geo.Freeze();

        var pen = new Media.Pen(brush, width)
        {
            StartLineCap = Media.PenLineCap.Round,
            EndLineCap = Media.PenLineCap.Round,
            LineJoin = Media.PenLineJoin.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}

internal sealed class NumberBadgeAnnotation : AnnotationItem
{
    public Wpf.Point Center { get; set; }
    public int Number { get; set; }

    public NumberBadgeAnnotation(AnnotationStyle style, Wpf.Point center, int number) : base(style)
    {
        Center = center;
        Number = number;
    }

    public override Wpf.Rect GetPhysicalBounds()
    {
        double r = Style.BadgeRadius;
        return new Wpf.Rect(Center.X - r, Center.Y - r, r * 2, r * 2);
    }

    public override bool HitTest(Wpf.Point physPt, double tolerance) =>
        Distance(physPt, Center) <= Style.BadgeRadius + Math.Max(4, tolerance);

    public override void Translate(double dx, double dy)
    {
        Center = new Wpf.Point(Center.X + dx, Center.Y + dy);
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        double r = Style.BadgeRadius;
        var fill = new Media.SolidColorBrush(Style.Color);
        fill.Freeze();
        var borderPen = new Media.Pen(Media.Brushes.White, 1.8);
        borderPen.Freeze();
        dc.DrawEllipse(fill, borderPen, Center, r, r);

        // Choose dark text on very bright badge colors (e.g., yellow/white).
        double lum = 0.299 * Style.Color.R + 0.587 * Style.Color.G + 0.114 * Style.Color.B;
        Media.Brush textBrush = lum > 205 ? Media.Brushes.Black : Media.Brushes.White;

        double fontSize = Number >= 10 ? r * 0.95 : r * 1.15;
        var ft = new Media.FormattedText(
            Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            Wpf.FlowDirection.LeftToRight,
            new Media.Typeface(new Media.FontFamily("Segoe UI"), Wpf.FontStyles.Normal, Wpf.FontWeights.Bold, Wpf.FontStretches.Normal),
            fontSize,
            textBrush,
            1.0);

        dc.DrawText(ft, new Wpf.Point(Center.X - ft.Width / 2.0, Center.Y - ft.Height / 2.0));
    }
}

internal sealed class MosaicAnnotation : AnnotationItem
{
    public List<Wpf.Point> Points { get; }
    public MosaicMode Mode { get; }
    public int Strength { get; }
    public double BrushWidth { get; }

    public MosaicAnnotation(
        AnnotationStyle style,
        MosaicMode mode,
        int strength,
        double brushWidth,
        IEnumerable<Wpf.Point> points) : base(style)
    {
        Mode = mode;
        Strength = strength;
        BrushWidth = brushWidth;
        Points = new List<Wpf.Point>(points);
    }

    public override Wpf.Rect GetPhysicalBounds()
    {
        if (Points.Count == 0) return Wpf.Rect.Empty;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (Wpf.Point p in Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        double pad = BrushWidth * 0.5;
        return new Wpf.Rect(minX - pad, minY - pad, (maxX - minX) + pad * 2, (maxY - minY) + pad * 2);
    }

    public override bool HitTest(Wpf.Point physPt, double tolerance)
    {
        if (Points.Count == 0) return false;
        double tol = BrushWidth * 0.5 + Math.Max(4, tolerance);
        if (Points.Count == 1) return Distance(physPt, Points[0]) <= tol;

        for (int i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(physPt, Points[i - 1], Points[i]) <= tol)
            {
                return true;
            }
        }
        return false;
    }

    public override void Translate(double dx, double dy)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new Wpf.Point(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        if (Points.Count == 0 || mosaicProvider is null) return;

        (Imaging.BitmapSource? bmp, CaptureRegion cacheRegion) = mosaicProvider(Mode, Strength);
        if (bmp is null || cacheRegion.Width <= 0 || cacheRegion.Height <= 0) return;

        var imgBrush = new Media.ImageBrush(bmp)
        {
            ViewportUnits = Media.BrushMappingMode.Absolute,
            Viewport = new Wpf.Rect(cacheRegion.X, cacheRegion.Y, cacheRegion.Width, cacheRegion.Height),
            TileMode = Media.TileMode.None,
            Stretch = Media.Stretch.Fill
        };
        imgBrush.Freeze();

        if (Points.Count == 1)
        {
            dc.DrawEllipse(imgBrush, null, Points[0], BrushWidth / 2.0, BrushWidth / 2.0);
            return;
        }

        var geo = new Media.StreamGeometry();
        using (Media.StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(Points[0], isFilled: false, isClosed: false);
            for (int i = 1; i < Points.Count; i++)
            {
                ctx.LineTo(Points[i], true, true);
            }
        }
        geo.Freeze();

        var pen = new Media.Pen(imgBrush, BrushWidth)
        {
            StartLineCap = Media.PenLineCap.Round,
            EndLineCap = Media.PenLineCap.Round,
            LineJoin = Media.PenLineJoin.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}

internal sealed class TextAnnotation : AnnotationItem
{
    public Wpf.Point Position { get; set; }
    public string Text { get; set; }

    public TextAnnotation(AnnotationStyle style, Wpf.Point position, string text) : base(style)
    {
        Position = position;
        Text = text;
    }

    private Media.FormattedText CreateFormattedText(Media.Brush brush) =>
        new(
            Text,
            CultureInfo.CurrentUICulture,
            Wpf.FlowDirection.LeftToRight,
            new Media.Typeface(new Media.FontFamily("Microsoft YaHei UI, Segoe UI"), Wpf.FontStyles.Normal, Wpf.FontWeights.SemiBold, Wpf.FontStretches.Normal),
            Style.FontSize,
            brush,
            1.0);

    public override Wpf.Rect GetPhysicalBounds()
    {
        if (string.IsNullOrEmpty(Text)) return Wpf.Rect.Empty;
        Media.FormattedText ft = CreateFormattedText(Media.Brushes.Black);
        return new Wpf.Rect(Position.X, Position.Y, Math.Max(12, ft.WidthIncludingTrailingWhitespace), Math.Max(Style.FontSize, ft.Height));
    }

    public override bool HitTest(Wpf.Point physPt, double tolerance)
    {
        Wpf.Rect r = GetPhysicalBounds();
        if (r.IsEmpty) return false;
        double tol = Math.Max(4, tolerance);
        return physPt.X >= r.Left - tol && physPt.X <= r.Right + tol &&
               physPt.Y >= r.Top - tol && physPt.Y <= r.Bottom + tol;
    }

    public override void Translate(double dx, double dy)
    {
        Position = new Wpf.Point(Position.X + dx, Position.Y + dy);
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        if (string.IsNullOrWhiteSpace(Text)) return;

        double lum = 0.299 * Style.Color.R + 0.587 * Style.Color.G + 0.114 * Style.Color.B;
        Media.Color shadowColor = lum < 90 ? Media.Color.FromArgb(140, 255, 255, 255) : Media.Color.FromArgb(150, 0, 0, 0);
        var shadowBrush = new Media.SolidColorBrush(shadowColor);
        shadowBrush.Freeze();
        Media.FormattedText shadowFt = CreateFormattedText(shadowBrush);
        dc.DrawText(shadowFt, new Wpf.Point(Position.X + 1.2, Position.Y + 1.2));

        var mainBrush = new Media.SolidColorBrush(Style.Color);
        mainBrush.Freeze();
        Media.FormattedText mainFt = CreateFormattedText(mainBrush);
        dc.DrawText(mainFt, Position);
    }
}

internal sealed class EmojiAnnotation : AnnotationItem
{
    public Wpf.Point Center { get; set; }
    public string Emoji { get; set; }

    public EmojiAnnotation(AnnotationStyle style, Wpf.Point center, string emoji) : base(style)
    {
        Center = center;
        Emoji = emoji;
    }

    public override Wpf.Rect GetPhysicalBounds()
    {
        double size = Style.EmojiSize;
        return new Wpf.Rect(Center.X - size / 2.0, Center.Y - size / 2.0, size, size);
    }

    public override bool HitTest(Wpf.Point physPt, double tolerance)
    {
        double half = Style.EmojiSize / 2.0 + Math.Max(4.0, tolerance * 0.6);
        return Math.Abs(physPt.X - Center.X) <= half && Math.Abs(physPt.Y - Center.Y) <= half;
    }

    public override void Translate(double dx, double dy)
    {
        Center = new Wpf.Point(Center.X + dx, Center.Y + dy);
    }

    public override void Draw(Media.DrawingContext dc, MosaicSourceProvider? mosaicProvider)
    {
        if (string.IsNullOrWhiteSpace(Emoji)) return;

        double size = Style.EmojiSize;
        var rect = new Wpf.Rect(Center.X - size / 2.0, Center.Y - size / 2.0, size, size);

        Imaging.BitmapSource? colorBmp = MiniRec.Native.ColorEmojiRenderer.RenderEmoji(Emoji, 128);
        if (colorBmp is not null)
        {
            dc.DrawImage(colorBmp, rect);
            return;
        }

        // 兜底回退（极少数不支持 Direct2D 彩色字体的老环境）
        var ft = new Media.FormattedText(
            Emoji,
            CultureInfo.CurrentUICulture,
            Wpf.FlowDirection.LeftToRight,
            new Media.Typeface(new Media.FontFamily("Segoe UI Emoji"), Wpf.FontStyles.Normal, Wpf.FontWeights.Normal, Wpf.FontStretches.Normal),
            size * 0.78,
            Media.Brushes.Black,
            1.0);
        dc.DrawText(ft, new Wpf.Point(Center.X - ft.Width / 2.0, Center.Y - ft.Height / 2.0));
    }
}
