using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Data;

namespace 模拟扫码枪;

/// <summary>
/// 枚举 -> 中文说明（取 <see cref="DescriptionAttribute"/>，没有则回退为枚举名）
/// </summary>
public class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        if (value is not Enum) return value.ToString() ?? string.Empty;

        var field = value.GetType().GetField(value.ToString()!);
        if (field is null) return value.ToString()!;

        var attr = field.GetCustomAttribute<DescriptionAttribute>();
        return attr?.Description ?? value.ToString()!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 枚举 -> 可见性。参数为逗号分隔的枚举名列表；前缀 "!" 表示取反。
/// 例：参数 "TcpClient,TcpServer" 表示匹配两者；参数 "!OnlyReceive" 表示不匹配时可见。
/// </summary>
public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var names = (parameter as string ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();

        if (names.Length == 0) return Visibility.Visible;

        var negate = names[0].StartsWith("!");
        if (negate) names = names.Select(x => x.TrimStart('!')).ToArray();

        var current = value?.ToString() ?? string.Empty;
        var hit = names.Any(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));

        var visible = negate ? !hit : hit;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 布尔 -> 可见性，参数 "Inverse" 时取反
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 空对象 -> 可见性（用于空状态占位）
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        if (string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase)) isNull = !isNull;
        return isNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 整数 -> 可见性。参数 "Zero" 表示值为 0 时可见；"NotZero" 表示值不为 0 时可见。
/// </summary>
public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        var isZero = n == 0;
        var showWhenZero = string.Equals(parameter as string, "Zero", StringComparison.OrdinalIgnoreCase);
        var visible = showWhenZero ? isZero : !isZero;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
