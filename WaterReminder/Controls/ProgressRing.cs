using System.Windows;
using System.Windows.Media;

namespace WaterReminder.Controls;

/// <summary>Circular progress ring with rounded caps. Progress is 0..1 and can be animated.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        var p = Math.Clamp(Progress, 0, 1);
        if (p <= 0.0005) return;
        var pen = new Pen(FillBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.9995)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = p * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, p > 0.5, SweepDirection.Clockwise, true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
