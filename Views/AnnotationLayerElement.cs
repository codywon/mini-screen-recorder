using MiniRec.Models;
using MiniRec.Services;
using Wpf = System.Windows;
using Media = System.Windows.Media;

namespace MiniRec.Views;

internal sealed class AnnotationLayerElement : Wpf.FrameworkElement
{
    public AnnotationEngine? Engine { get; set; }
    public AnnotationItem? PreviewItem { get; set; }
    public Media.Matrix PhysToDipMatrix { get; set; } = Media.Matrix.Identity;
    public Wpf.Rect ClipDipRect { get; set; } = Wpf.Rect.Empty;
    public MosaicSourceProvider? MosaicProvider { get; set; }

    public AnnotationLayerElement()
    {
        IsHitTestVisible = false;
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(Media.DrawingContext dc)
    {
        base.OnRender(dc);
        if (Engine is null) return;

        bool hasClip = ClipDipRect is { IsEmpty: false, Width: > 0, Height: > 0 };
        if (hasClip)
        {
            dc.PushClip(new Media.RectangleGeometry(ClipDipRect));
        }

        dc.PushTransform(new Media.MatrixTransform(PhysToDipMatrix));

        foreach (AnnotationItem item in Engine.Items)
        {
            item.Draw(dc, MosaicProvider);
        }

        PreviewItem?.Draw(dc, MosaicProvider);

        if (Engine.SelectedItem is not null)
        {
            double invScale = PhysToDipMatrix.M11 > 0.01 ? 1.0 / PhysToDipMatrix.M11 : 1.0;
            Engine.SelectedItem.DrawSelectionOutline(dc, invScale);
        }

        dc.Pop();

        if (hasClip)
        {
            dc.Pop();
        }
    }
}
