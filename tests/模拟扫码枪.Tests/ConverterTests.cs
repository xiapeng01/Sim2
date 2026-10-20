using System.Globalization;
using System.Windows;
using System.Windows.Data;
using 模拟扫码枪;

namespace 模拟扫码枪.Tests;

/// <summary>UI 转换器的单元测试（UI 显隐逻辑的回归防线）</summary>
public class ConverterTests
{
    private static readonly EnumDescriptionConverter EnumDesc = new();
    private static readonly EnumToVisibilityConverter EnumVis = new();
    private static readonly BoolToVisibilityConverter BoolVis = new();
    private static readonly NullToVisibilityConverter NullVis = new();
    private static readonly IntToVisibilityConverter IntVis = new();
    private static readonly BoolReverseConverter Reverse = new();

    private static object? Convert(IValueConverter c, object? value, object? parameter)
        => c.Convert(value, typeof(Visibility), parameter, CultureInfo.InvariantCulture);

    // ---------- EnumDescriptionConverter ----------

    [Fact]
    public void 枚举转换器_应返回中文描述()
    {
        Assert.Equal("TCP 服务端", Convert(EnumDesc, EnumInterfaceType.TcpServer, null));
    }

    [Fact]
    public void 枚举转换器_null应返回空串()
    {
        Assert.Equal(string.Empty, Convert(EnumDesc, null, null));
    }

    [Fact]
    public void 枚举转换器_非枚举应返回其字符串()
    {
        Assert.Equal("123", Convert(EnumDesc, 123, null));
    }

    [Fact]
    public void 枚举转换器_ConvertBack应不修改()
    {
        Assert.Equal(Binding.DoNothing, EnumDesc.ConvertBack("x", typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    // ---------- EnumToVisibilityConverter ----------

    [Fact]
    public void 枚举可见性_匹配应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(EnumVis, EnumInterfaceType.TcpClient, "TcpClient,TcpServer"));
    }

    [Fact]
    public void 枚举可见性_列表内任一匹配应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(EnumVis, EnumInterfaceType.TcpServer, "TcpClient,TcpServer"));
    }

    [Fact]
    public void 枚举可见性_不匹配应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(EnumVis, EnumInterfaceType.SerialPort, "TcpClient,TcpServer"));
    }

    [Fact]
    public void 枚举可见性_取反参数_不匹配应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(EnumVis, EnumWorkModel.OnlySend, "!OnlyReceive"));
    }

    [Fact]
    public void 枚举可见性_取反参数_匹配应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(EnumVis, EnumWorkModel.OnlyReceive, "!OnlyReceive"));
    }

    [Fact]
    public void 枚举可见性_无参数应默认可见()
    {
        Assert.Equal(Visibility.Visible, Convert(EnumVis, EnumWorkModel.Passive, null));
    }

    [Fact]
    public void 枚举可见性_null值应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(EnumVis, null, "TcpClient"));
    }

    [Fact]
    public void 枚举可见性_应忽略大小写()
    {
        Assert.Equal(Visibility.Visible, Convert(EnumVis, EnumInterfaceType.TcpClient, "tcpclient"));
    }

    // ---------- BoolToVisibilityConverter ----------

    [Fact]
    public void 布尔可见性_true应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(BoolVis, true, null));
    }

    [Fact]
    public void 布尔可见性_取反参数true应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(BoolVis, true, "Inverse"));
    }

    [Fact]
    public void 布尔可见性_取反参数false应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(BoolVis, false, "Inverse"));
    }

    [Fact]
    public void 布尔可见性_null应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(BoolVis, null, null));
    }

    // ---------- NullToVisibilityConverter ----------

    [Fact]
    public void 空对象可见性_null应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(NullVis, null, null));
    }

    [Fact]
    public void 空对象可见性_有值应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(NullVis, new object(), null));
    }

    [Fact]
    public void 空对象可见性_Inverse取反()
    {
        Assert.Equal(Visibility.Collapsed, Convert(NullVis, null, "Inverse"));
        Assert.Equal(Visibility.Visible, Convert(NullVis, new object(), "Inverse"));
    }

    // ---------- IntToVisibilityConverter ----------

    [Fact]
    public void 整数可见性_Zero参数零应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(IntVis, 0, "Zero"));
    }

    [Fact]
    public void 整数可见性_Zero参数非零应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(IntVis, 3, "Zero"));
    }

    [Fact]
    public void 整数可见性_默认非零应可见()
    {
        Assert.Equal(Visibility.Visible, Convert(IntVis, 3, null));
    }

    [Fact]
    public void 整数可见性_默认零应折叠()
    {
        Assert.Equal(Visibility.Collapsed, Convert(IntVis, 0, null));
    }

    [Fact]
    public void 整数可见性_null按零处理()
    {
        Assert.Equal(Visibility.Collapsed, Convert(IntVis, null, null));
    }

    // ---------- BoolReverseConverter（报告 P0 前修复项） ----------

    [Fact]
    public void 取反转换器_true应变false()
    {
        Assert.Equal(false, Convert(Reverse, true, null));
    }

    [Fact]
    public void 取反转换器_false应变true()
    {
        Assert.Equal(true, Convert(Reverse, false, null));
    }

    /// <summary>修复前此用例会因强转 bool 抛 InvalidCastException</summary>
    [Fact]
    public void 取反转换器_null不应抛异常()
    {
        var ex = Record.Exception(() => Convert(Reverse, null, null));
        Assert.Null(ex);
    }

    [Fact]
    public void 取反转换器_ConvertBack也应取反()
    {
        Assert.Equal(false, Reverse.ConvertBack(true, typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
