using System.ComponentModel;

namespace 模拟扫码枪.Tests;

/// <summary>ScannerWrap：接口切换时重建底层对象并保留参数</summary>
public class ScannerWrapTests
{
    [Fact]
    public void 默认应为串口类型()
    {
        using var w = new ScannerWrap();
        Assert.Equal(EnumInterfaceType.SerialPort, w.InterfaceType);
        Assert.IsType<CommSerialPort>(w.Scanner);
    }

    [Theory]
    [InlineData(EnumInterfaceType.SerialPort, typeof(CommSerialPort))]
    [InlineData(EnumInterfaceType.TcpClient, typeof(CommTcpClient))]
    [InlineData(EnumInterfaceType.TcpServer, typeof(CommTcpServer))]
    public void 切换接口类型应重建对应实例(EnumInterfaceType type, Type expected)
    {
        using var w = new ScannerWrap();
        w.InterfaceType = type;
        Assert.IsType(expected, w.Scanner);
        Assert.Equal(type, w.Scanner.InterfaceType);
    }

    [Fact]
    public void 切换接口类型应保留用户已填参数()
    {
        using var w = new ScannerWrap();
        w.Scanner.Name = "我的扫码枪";
        w.Scanner.TcpIP = "192.168.1.10";
        w.Scanner.TcpPort = 12345;
        w.Scanner.SendContent = "PAYLOAD";
        w.Scanner.WorkModel = EnumWorkModel.OnlySend;
        w.Scanner.EndOfFrame = EnumEndOfFrame.CRLR;

        w.InterfaceType = EnumInterfaceType.TcpServer;

        Assert.Equal("我的扫码枪", w.Scanner.Name);
        Assert.Equal("192.168.1.10", w.Scanner.TcpIP);
        Assert.Equal(12345, w.Scanner.TcpPort);
        Assert.Equal("PAYLOAD", w.Scanner.SendContent);
        Assert.Equal(EnumWorkModel.OnlySend, w.Scanner.WorkModel);
        Assert.Equal(EnumEndOfFrame.CRLR, w.Scanner.EndOfFrame);
    }

    [Fact]
    public void 重复设置相同接口类型不应重建实例()
    {
        using var w = new ScannerWrap();
        var before = w.Scanner;
        w.InterfaceType = EnumInterfaceType.SerialPort;
        Assert.Same(before, w.Scanner);
    }

    [Fact]
    public void 切换接口类型应触发ScannerReplaced事件()
    {
        using var w = new ScannerWrap();
        int count = 0;
        w.ScannerReplaced += (_, _) => count++;

        w.InterfaceType = EnumInterfaceType.TcpClient;
        Assert.Equal(1, count);
    }

    [Fact]
    public void 内外层接口类型应始终一致()
    {
        using var w = new ScannerWrap();
        foreach (var t in new[]
                 {
                     EnumInterfaceType.TcpServer,
                     EnumInterfaceType.SerialPort,
                     EnumInterfaceType.TcpClient
                 })
        {
            w.InterfaceType = t;
            Assert.Equal(t, w.InterfaceType);
            Assert.Equal(t, w.Scanner.InterfaceType);
        }
    }

    [Fact]
    public void Dispose后应标记为已释放()
    {
        var w = new ScannerWrap();
        w.Dispose();
        var ex = Record.Exception(() => w.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Scanner列表值应来自对应枚举()
    {
        using var w = new ScannerWrap();
        Assert.Equal(Enum.GetValues<EnumInterfaceType>().Length, w.Scanner.InterfaceTypeValues.Length);
        Assert.Equal(Enum.GetValues<EnumWorkModel>().Length, w.Scanner.WorkModelTypes.Length);
        Assert.Equal(Enum.GetValues<EnumAppendContent>().Length, w.Scanner.AppendContentValues.Length);
        Assert.Equal(Enum.GetValues<EnumEndOfFrame>().Length, w.Scanner.EndOfFramesValues.Length);
        Assert.Equal(Enum.GetValues<EnumContentType>().Length, w.Scanner.ContentTypesValues.Length);
    }

    [Fact]
    public void 串口列表应包含空选项()
    {
        using var w = new ScannerWrap();
        Assert.Equal("", w.Scanner.SerialPortNameValues[0]);
    }
}

/// <summary>枚举的中文描述（UI 依赖）</summary>
public class EnumDescriptionTests
{
    [Theory]
    [InlineData(EnumInterfaceType.SerialPort, "串口")]
    [InlineData(EnumInterfaceType.TcpClient, "TCP 客户端")]
    [InlineData(EnumInterfaceType.TcpServer, "TCP 服务端")]
    public void 接口类型应有中文描述(EnumInterfaceType value, string expected)
    {
        Assert.Equal(expected, GetDescription(value));
    }

    [Theory]
    [InlineData(EnumWorkModel.Trigger, "主动触发")]
    [InlineData(EnumWorkModel.Passive, "被动接收")]
    [InlineData(EnumWorkModel.OnlySend, "仅发送")]
    [InlineData(EnumWorkModel.OnlyReceive, "仅接收")]
    public void 工作模式应有中文描述(EnumWorkModel value, string expected)
    {
        Assert.Equal(expected, GetDescription(value));
    }

    [Theory]
    [InlineData(EnumAppendContent.None, "不附加")]
    [InlineData(EnumAppendContent.Random, "随机数")]
    [InlineData(EnumAppendContent.OrderNumber, "序列号")]
    public void 附加内容应有中文描述(EnumAppendContent value, string expected)
    {
        Assert.Equal(expected, GetDescription(value));
    }

    [Theory]
    [InlineData(EnumContentType.Text, "文本")]
    [InlineData(EnumContentType.Hex, "HEX 十六进制")]
    public void 内容类型应有中文描述(EnumContentType value, string expected)
    {
        Assert.Equal(expected, GetDescription(value));
    }

    [Theory]
    [InlineData(EnumEndOfFrame.None, "无")]
    [InlineData(EnumEndOfFrame.CR, "CR (\\r)")]
    [InlineData(EnumEndOfFrame.LR, "LF (\\n)")]
    [InlineData(EnumEndOfFrame.CRLR, "CRLF (\\r\\n)")]
    public void 结束符应有中文描述(EnumEndOfFrame value, string expected)
    {
        Assert.Equal(expected, GetDescription(value));
    }

    private static string GetDescription(Enum value)
    {
        var field = value.GetType().GetField(value.ToString()!);
        Assert.NotNull(field);
        var attr = field!.GetCustomAttributes(typeof(DescriptionAttribute), false)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault();
        Assert.NotNull(attr);
        return attr!.Description;
    }
}
