using System.Text;

namespace 模拟扫码枪.Tests;

/// <summary>
/// 公开 AComm 的受保护成员，便于对纯逻辑做单元测试。
/// 这里只验证不涉及真实通信的算法分支。
/// </summary>
public class TestableComm : AComm
{
    public byte[] InvokeHexStringToByteArray(string str) => HexStringToByteArray(str);
    public bool InvokeCompareByteArray(byte[] a, byte[] b) => CompareByteArray(a, b);
    public string InvokeGetEOF() => GetEOF();
    public string InvokeGetAppendContent() => GetAppendContent();

    /// <summary>暴露受保护的释放标记，便于断言 Dispose 行为</summary>
    public bool DisposedFlag => IsDisposed;

    // AComm 的抽象成员在单元测试中无需真实实现
    public override string Trigger() => string.Empty;
    public override bool Send() => false;
    public override bool SendString(string str) => false;
    public override bool SendData(byte[] data) => false;
    public override bool Cancel() => false;
}

/// <summary>AComm 基类的纯逻辑单元测试</summary>
public class ACommLogicTests
{
    private readonly TestableComm _comm = new();

    // ---------- HexStringToByteArray ----------

    [Theory]
    [InlineData("16540D", new byte[] { 0x16, 0x54, 0x0D })]
    [InlineData("16540d", new byte[] { 0x16, 0x54, 0x0D })]        // 小写
    [InlineData("16 54 0D", new byte[] { 0x16, 0x54, 0x0D })]      // 含空格
    [InlineData("AB", new byte[] { 0xAB })]
    public void HexStringToByteArray_应正确解析十六进制(string input, byte[] expected)
    {
        Assert.Equal(expected, _comm.InvokeHexStringToByteArray(input));
    }

    [Fact]
    public void HexStringToByteArray_空字符串应抛异常()
    {
        Assert.ThrowsAny<Exception>(() => _comm.InvokeHexStringToByteArray(""));
    }

    [Fact]
    public void HexStringToByteArray_奇数长度应在末尾补0()
    {
        // 现状行为（报告 P1 #18 指出其静默容错）："165" -> "1650"
        Assert.Equal(new byte[] { 0x16, 0x50 }, _comm.InvokeHexStringToByteArray("165"));
    }

    [Fact]
    public void HexStringToByteArray_非法字符应抛异常()
    {
        Assert.ThrowsAny<Exception>(() => _comm.InvokeHexStringToByteArray("ZZ"));
    }

    // ---------- CompareByteArray ----------

    [Fact]
    public void CompareByteArray_内容相同应返回true()
    {
        Assert.True(_comm.InvokeCompareByteArray(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void CompareByteArray_内容不同应返回false()
    {
        Assert.False(_comm.InvokeCompareByteArray(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }));
    }

    [Fact]
    public void CompareByteArray_长度不同应返回false()
    {
        Assert.False(_comm.InvokeCompareByteArray(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData(new byte[0], new byte[0])]
    [InlineData(new byte[0], new byte[] { 1 })]
    [InlineData(new byte[] { 1 }, new byte[0])]
    public void CompareByteArray_任一为空应返回false(byte[] a, byte[] b)
    {
        Assert.False(_comm.InvokeCompareByteArray(a, b));
    }

    // ---------- GetEOF ----------

    [Theory]
    [InlineData(EnumEndOfFrame.None, "")]
    [InlineData(EnumEndOfFrame.CR, "\r")]
    [InlineData(EnumEndOfFrame.LR, "\n")]
    [InlineData(EnumEndOfFrame.CRLR, "\r\n")]
    public void GetEOF_应按枚举返回对应结束符(EnumEndOfFrame frame, string expected)
    {
        _comm.EndOfFrame = frame;
        Assert.Equal(expected, _comm.InvokeGetEOF());
    }

    // ---------- GetAppendContent ----------

    [Fact]
    public void GetAppendContent_不附加应返回空串()
    {
        _comm.AppendContent = EnumAppendContent.None;
        Assert.Equal("", _comm.InvokeGetAppendContent());
    }

    [Fact]
    public void GetAppendContent_随机数长度应等于配置长度()
    {
        _comm.AppendContent = EnumAppendContent.Random;
        _comm.AppendContentLength = 6;
        var content = _comm.InvokeGetAppendContent();
        Assert.Equal(6, content.Length);
        Assert.True(content.All(char.IsDigit));
    }

    [Fact]
    public void GetAppendContent_序列号应递增()
    {
        _comm.AppendContent = EnumAppendContent.OrderNumber;
        _comm.AppendContentLength = 6;
        _comm.OrderNumber = 0;

        var first = _comm.InvokeGetAppendContent();
        var second = _comm.InvokeGetAppendContent();

        Assert.Equal(6, first.Length);
        Assert.Equal(6, second.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GetAppendContent_解析发送应取竖线分隔的某一段()
    {
        _comm.AppendContent = EnumAppendContent.ParseAndSend;
        _comm.SendContent = "AAA|BBB|CCC";

        var content = _comm.InvokeGetAppendContent();
        Assert.Contains(content, new[] { "AAA", "BBB", "CCC" });
    }

    /// <summary>
    /// 报告 P1 #6：附加内容长度超过 10 时，PadLeft(10) 后的字符串长度不足，
    /// Substring 起始索引为负 → 抛 ArgumentOutOfRangeException。
    /// 本用例把该缺陷固化为可观测行为，修复后应改为通过。
    /// </summary>
    [Fact]
    public void GetAppendContent_长度大于10时被钳制为10且不抛异常()
    {
        _comm.AppendContent = EnumAppendContent.Random;
        _comm.AppendContentLength = 20;
        // 修复前：PadLeft(10) 固定 10 位，Substring(10-20=-5) 抛 ArgumentOutOfRangeException，
        // 异常被 Send() 捕获后触发 Close()，用户只是填错长度却把连接踢掉。
        // 现在 setter 钳制为 1~10，兜底处再钳一次，任何途径都不会越界。
        Assert.Equal(10, _comm.AppendContentLength);
        var result = _comm.InvokeGetAppendContent();
        Assert.Equal(10, result.Length);
        Assert.All(result, c => Assert.True(char.IsDigit(c)));
    }

    // ---------- 属性通知 ----------

    [Fact]
    public void 设置相同值不应触发PropertyChanged()
    {
        _comm.Name = "测试";
        int count = 0;
        _comm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AComm.Name)) Interlocked.Increment(ref count);
        };

        _comm.Name = "测试";
        Assert.Equal(0, count);
    }

    [Fact]
    public void 设置新值应触发PropertyChanged()
    {
        _comm.Name = "初始";
        int count = 0;
        _comm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AComm.Name)) Interlocked.Increment(ref count);
        };

        _comm.Name = "修改后";
        Assert.Equal(1, count);
    }

    [Fact]
    public void 修改IsEnabled应触发PropertyChanged()
    {
        int count = 0;
        _comm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AComm.IsEnabled)) Interlocked.Increment(ref count);
        };

        _comm.IsEnabled = true;
        Assert.Equal(1, count);
    }

    // ---------- CopySettingsTo ----------

    [Fact]
    public void CopySettingsTo_应复制全部配置()
    {
        var source = new TestableComm
        {
            Name = "源",
            SerialPortName = "COM3",
            SerialPortBaudRate = 9600,
            TcpIP = "10.0.0.5",
            TcpPort = 9999,
            WorkModel = EnumWorkModel.OnlySend,
            SendContent = "ABC",
            EndOfFrame = EnumEndOfFrame.CRLR,
            AppendContent = EnumAppendContent.Random,
            AppendContentLength = 4,
            WriteTimeOut = 1234,
            ReadTimeOut = 4321,
            IsVerifyTriggerContent = false
        };

        var target = new TestableComm();
        source.CopySettingsTo(target);

        Assert.Equal("源", target.Name);
        Assert.Equal("COM3", target.SerialPortName);
        Assert.Equal(9600, target.SerialPortBaudRate);
        Assert.Equal("10.0.0.5", target.TcpIP);
        Assert.Equal(9999, target.TcpPort);
        Assert.Equal(EnumWorkModel.OnlySend, target.WorkModel);
        Assert.Equal("ABC", target.SendContent);
        Assert.Equal(EnumEndOfFrame.CRLR, target.EndOfFrame);
        Assert.Equal(EnumAppendContent.Random, target.AppendContent);
        Assert.Equal(4, target.AppendContentLength);
        Assert.Equal(1234, target.WriteTimeOut);
        Assert.Equal(4321, target.ReadTimeOut);
        Assert.False(target.IsVerifyTriggerContent);
    }

    [Fact]
    public void CopySettingsTo_传入null应安全返回()
    {
        var t = new TestableComm();
        t.CopySettingsTo(null!);
    }

    // ---------- Dispose ----------

    [Fact]
    public void Dispose_应置IsEnabled与IsDisposed()
    {
        var c = new TestableComm { IsEnabled = true };
        c.Dispose();
        Assert.False(c.IsEnabled);
        Assert.True(c.DisposedFlag);
    }

    [Fact]
    public void Dispose_应幂等()
    {
        var c = new TestableComm();
        c.Dispose();
        var ex = Record.Exception(() => c.Dispose());
        Assert.Null(ex);
    }
}
