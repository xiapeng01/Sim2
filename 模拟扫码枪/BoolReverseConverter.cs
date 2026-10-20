using System;
using System.Globalization;
using System.Windows.Data;

namespace 模拟扫码枪
{
    /// <summary>
    /// 取反布尔值。原实现直接强转 bool，绑定值为 null 时会抛异常。
    /// </summary>
    public class BoolReverseConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool b && !b;
        }
    }
}
