using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WaterReminder.Controls;

/// <summary>Numeric / time stepper: [-] [ value ] [+]. Time values are minutes since midnight.</summary>
public sealed class Stepper : Grid
{
    private readonly TextBox _box;
    private readonly Button _minus;
    private readonly Button _plus;
    private int _value;

    public bool IsTime { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100000;
    public int Step { get; set; } = 1;
    public string Suffix { get; set; } = "";
    public bool IsValid { get; private set; } = true;
    public event EventHandler? ValueChanged;

    public int Value
    {
        get => _value;
        set { _value = Math.Clamp(value, Minimum, Maximum); IsValid = true; _box.Text = Format(_value); _box.Tag = null; ValueChanged?.Invoke(this, EventArgs.Empty); }
    }

    public Stepper()
    {
        Height = 36;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _minus = MakeButton("\uE738", "减少");
        _plus = MakeButton("\uE710", "增加");
        _box = new TextBox { TextAlignment = TextAlignment.Center, Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        _box.SetResourceReference(StyleProperty, "FluentTextBox");
        SetColumn(_minus, 0); SetColumn(_box, 1); SetColumn(_plus, 2);
        Children.Add(_minus); Children.Add(_box); Children.Add(_plus);
        _minus.Click += (_, _) => Bump(-1);
        _plus.Click += (_, _) => Bump(1);
        _box.TextChanged += (_, _) => Parse(false);
        _box.LostKeyboardFocus += (_, _) => { if (Parse(true)) _box.Text = Format(_value); };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up) { Bump(1); e.Handled = true; }
            else if (e.Key == Key.Down) { Bump(-1); e.Handled = true; }
        };
        _box.MouseWheel += (_, e) => { if (_box.IsKeyboardFocusWithin) { Bump(e.Delta > 0 ? 1 : -1); e.Handled = true; } };
        _box.GotKeyboardFocus += (_, _) => _box.SelectAll();
    }

    private Button MakeButton(string glyph, string tip)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Width = 36, Height = 36 };
        b.SetResourceReference(StyleProperty, "StepperButton");
        return b;
    }

    private void Bump(int dir)
    {
        Parse(true);
        var baseValue = IsValid ? _value : Math.Clamp(_value, Minimum, Maximum);
        var next = dir > 0 ? (baseValue / Step + 1) * Step : ((baseValue + Step - 1) / Step - 1) * Step;
        Value = next;
    }

    private bool Parse(bool commit)
    {
        var text = _box.Text.Trim();
        if (!string.IsNullOrEmpty(Suffix) && text.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) text = text[..^Suffix.Length].Trim();
        int parsed;
        bool ok;
        if (IsTime)
        {
            ok = TryParseTime(text, out parsed);
        }
        else
        {
            ok = int.TryParse(text.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        }
        ok = ok && parsed >= Minimum && parsed <= Maximum;
        IsValid = ok;
        _box.Tag = ok ? null : "error";
        if (ok && parsed != _value) { _value = parsed; ValueChanged?.Invoke(this, EventArgs.Empty); }
        else if (!ok) ValueChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    private static bool TryParseTime(string text, out int minutes)
    {
        minutes = 0;
        text = text.Replace('：', ':');
        if (TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm", @"h\:m" }, CultureInfo.InvariantCulture, out var ts) && ts.TotalHours < 24)
        { minutes = (int)ts.TotalMinutes; return true; }
        if (text.Length is 3 or 4 && int.TryParse(text, out var raw) && raw / 100 < 24 && raw % 100 < 60)
        { minutes = raw / 100 * 60 + raw % 100; return true; }
        return false;
    }

    private string Format(int v) => IsTime ? $"{v / 60:00}:{v % 60:00}" : v.ToString(CultureInfo.InvariantCulture) + (Suffix.Length > 0 ? " " + Suffix : "");
}
