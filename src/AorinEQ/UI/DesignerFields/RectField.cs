using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;

namespace AorinEQ.UI.DesignerFields;

/// <summary>A rectangle as four number boxes, X and Y over W and H, each ranged to the area the
/// rectangle lives in. Any one changing reports the whole rectangle.</summary>
internal sealed class RectField : Grid
{
    public RectField(string x, string y, string width, string height, int areaWidth, int areaHeight)
    {
        X = new NumberField(x, 0, areaWidth);
        Y = new NumberField(y, 0, areaHeight);
        W = new NumberField(width, 0, areaWidth);
        H = new NumberField(height, 0, areaHeight);
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        RowDefinitions.Add(new RowDefinition());
        Place(X, 0, 0);
        Place(Y, 0, 2);
        Place(W, 2, 0);
        Place(H, 2, 2);
    }

    public NumberField X { get; }
    public NumberField Y { get; }
    public NumberField W { get; }
    public NumberField H { get; }

    public SkinHitRect Value => new((int)X.Value, (int)Y.Value, (int)W.Value, (int)H.Value);

    public bool IsEditing => X.IsEditing || Y.IsEditing || W.IsEditing || H.IsEditing;

    public event Action<SkinHitRect>? ValueChanged;

    public event Action? EditEnded;

    /// <summary>The area the rectangle lives in changed size: each box's range follows in place.</summary>
    public void SetArea(int areaWidth, int areaHeight)
    {
        X.SetRange(0, areaWidth);
        Y.SetRange(0, areaHeight);
        W.SetRange(0, areaWidth);
        H.SetRange(0, areaHeight);
    }

    public void ShowValue(SkinHitRect rect)
    {
        X.ShowValue(rect.X);
        Y.ShowValue(rect.Y);
        W.ShowValue(rect.Width);
        H.ShowValue(rect.Height);
    }

    private void Place(NumberField field, int row, int column)
    {
        SetRow(field, row);
        SetColumn(field, column);
        field.ValueChanged += _ => ValueChanged?.Invoke(Value);
        field.EditEnded += () => EditEnded?.Invoke();
        Children.Add(field);
    }
}
