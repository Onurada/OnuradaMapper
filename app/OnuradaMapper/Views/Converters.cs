using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OnuradaMapper.Services;

namespace OnuradaMapper.Views;

public sealed class StepGlyph : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => (StepState)v switch
    {
        StepState.Running => "", StepState.Done => "", StepState.Skipped => "",
        StepState.Failed => "", _ => "",
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StepBrush : IValueConverter
{
    static readonly Brush Run = Freeze("#FF4FA3"), Ok = Freeze("#2BB673"), Skip = Freeze("#8A8A8A"), Bad = Freeze("#E5484D"), Wait = Freeze("#9A9A9A");
    static Brush Freeze(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    public object Convert(object v, Type t, object p, CultureInfo c) => v switch
    {
        StepState.Running => Run, StepState.Done => Ok, StepState.Skipped => Skip, StepState.Failed => Bad, StepState s => Wait,
        true => Ok, false => Bad, _ => Wait,
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class OkGlyph : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is true ? "" : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>int mode ⇄ RadioButton.IsChecked (ConverterParameter = the mode's number).</summary>
public sealed class IntIs : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is int i && i.ToString() == (string)p;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is true ? int.Parse((string)p) : Binding.DoNothing;
}

/// <summary>int mode == parameter → Visible.</summary>
public sealed class IntVis : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        (v is int or Enum) && ((string)p).Split(',', '|').Contains(System.Convert.ToInt32(v).ToString()) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c) => (v is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Not : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is not true;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is not true;
}

public sealed class HexBrush : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + ((string)v).Trim().TrimStart('#'))); }
        catch { return Brushes.Transparent; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EmptyVis : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => string.IsNullOrEmpty(v as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class TaskbarState : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is true ? System.Windows.Shell.TaskbarItemProgressState.Normal : System.Windows.Shell.TaskbarItemProgressState.None;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Fraction : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is double d ? d / 100.0 : 0.0;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Percent : IValueConverter
{
    // -1 = indeterminate
    public object Convert(object v, Type t, object p, CultureInfo c) => v is double d && d < 0;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
