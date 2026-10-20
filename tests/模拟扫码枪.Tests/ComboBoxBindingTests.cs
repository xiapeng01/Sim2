using System.IO.Ports;

namespace 模拟扫码枪.Tests;

/// <summary>
/// ComboBox 绑定健壮性测试。
///
/// <para>背景：属性面板的 ComboBox 用 <c>SelectedItem</c> 双向绑定到源属性。
/// 当源值不在 <c>ItemsSource</c> 列表中时，WPF 会把 <c>SelectedItem</c> 置为 null，
/// 而模板只显示 <c>SelectionBoxItem</c>，于是界面上表现为「空白」——
/// 用户以为配置丢了，实际上源值还在。</para>
///
/// <para>修复策略：候选列表必须始终包含当前值；枚举类属性还要对非法值做归一化。</para>
/// </summary>
public class ComboBoxBindingTests
{
    private static TestableComm Create() => new();

    // ---------- 串口号 ----------

    [Fact]
    public void SerialPortNameValues_应始终包含当前串口号()
    {
        var c = Create();
        c.SerialPortName = "COM99"; // 现实中几乎不可能存在的端口
        Assert.Contains("COM99", c.SerialPortNameValues);
    }

    [Fact]
    public void SerialPortNameValues_当前值已在系统端口列表中时不应重复()
    {
        var c = Create();
        var names = SerialPort.GetPortNames();
        if (names.Length == 0) return; // 无串口机器上跳过
        c.SerialPortName = names[0];
        var list = c.SerialPortNameValues;
        Assert.Equal(1, list.Count(n => n == names[0]));
    }

    [Fact]
    public void SerialPortNameValues_应始终以空串开头()
    {
        var c = Create();
        Assert.Equal("", c.SerialPortNameValues[0]);
    }

    [Fact]
    public void SerialPortName_赋null应保留原值而非写入null()
    {
        var c = Create();
        c.SerialPortName = "COM7";
        c.SerialPortName = null!;
        Assert.Equal("COM7", c.SerialPortName);
    }

    [Fact]
    public void SerialPortName_变更后应通知候选列表刷新()
    {
        var c = Create();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.SerialPortName = "COM42";

        Assert.Contains(nameof(TestableComm.SerialPortNameValues), changed);
    }

    // ---------- 波特率 ----------

    [Theory]
    [InlineData(9600)]
    [InlineData(115200)]
    [InlineData(12345)]      // 非标准波特率
    public void SerialPortBaudRateValues_应始终包含当前波特率(int baud)
    {
        var c = Create();
        c.SerialPortBaudRate = baud;
        Assert.Contains(baud, c.SerialPortBaudRateValues);
    }

    [Fact]
    public void SerialPortBaudRateValues_非法值0应被忽略()
    {
        var c = Create();
        c.SerialPortBaudRate = 9600;
        c.SerialPortBaudRate = 0;
        Assert.Equal(9600, c.SerialPortBaudRate);
    }

    [Fact]
    public void SerialPortBaudRateValues_负值应被忽略()
    {
        var c = Create();
        c.SerialPortBaudRate = 4800;
        c.SerialPortBaudRate = -1;
        Assert.Equal(4800, c.SerialPortBaudRate);
    }

    [Fact]
    public void SerialPortBaudRate_变更后应通知候选列表刷新()
    {
        var c = Create();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.SerialPortBaudRate = 57600;

        Assert.Contains(nameof(TestableComm.SerialPortBaudRateValues), changed);
    }

    // ---------- 数据位 ----------

    [Fact]
    public void SerialPortDataBitsValues_应始终包含当前数据位()
    {
        var c = Create();
        c.SerialPortDataBits = 8;
        Assert.Contains(8, c.SerialPortDataBitsValues);
    }

    [Fact]
    public void SerialPortDataBits_非法值应被忽略()
    {
        var c = Create();
        c.SerialPortDataBits = 8;
        c.SerialPortDataBits = 5;
        Assert.Equal(8, c.SerialPortDataBits);
    }

    [Fact]
    public void SerialPortDataBits_变更后应通知候选列表刷新()
    {
        var c = Create();
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        c.SerialPortDataBits = 7;

        Assert.Contains(nameof(TestableComm.SerialPortDataBitsValues), changed);
    }

    // ---------- 枚举类属性的非法值归一化 ----------

    [Fact]
    public void WorkModel_非法枚举值应归一化为合法值()
    {
        var c = Create();
        c.WorkModel = (EnumWorkModel)999;
        Assert.True(Enum.IsDefined(typeof(EnumWorkModel), c.WorkModel));
    }

    [Fact]
    public void AppendContent_非法枚举值应归一化为合法值()
    {
        var c = Create();
        c.AppendContent = (EnumAppendContent)999;
        Assert.True(Enum.IsDefined(typeof(EnumAppendContent), c.AppendContent));
    }

    [Fact]
    public void EndOfFrame_非法枚举值应归一化为合法值()
    {
        var c = Create();
        c.EndOfFrame = (EnumEndOfFrame)999;
        Assert.True(Enum.IsDefined(typeof(EnumEndOfFrame), c.EndOfFrame));
    }

    [Fact]
    public void SendContentType_非法枚举值应归一化为合法值()
    {
        var c = Create();
        c.SendContentType = (EnumContentType)999;
        Assert.True(Enum.IsDefined(typeof(EnumContentType), c.SendContentType));
    }

    [Fact]
    public void ReceiveContentType_非法枚举值应归一化为合法值()
    {
        var c = Create();
        c.ReceiveContentType = (EnumContentType)999;
        Assert.True(Enum.IsDefined(typeof(EnumContentType), c.ReceiveContentType));
    }

    [Fact]
    public void SerialPortParity_非法枚举值应归一化为合法值()
    {
        var c = Create();
        c.SerialPortParity = (Parity)999;
        Assert.True(Enum.IsDefined(typeof(Parity), c.SerialPortParity));
    }

    [Fact]
    public void SerialPortStopBits_非法枚举值应归一化为合法值()
    {
        var c = Create();
        c.SerialPortStopBits = (StopBits)999;
        Assert.True(Enum.IsDefined(typeof(StopBits), c.SerialPortStopBits));
    }

    [Fact]
    public void 合法枚举值不应被归一化改变()
    {
        var c = Create();
        c.WorkModel = EnumWorkModel.OnlyReceive;
        Assert.Equal(EnumWorkModel.OnlyReceive, c.WorkModel);

        c.SerialPortStopBits = StopBits.Two;
        Assert.Equal(StopBits.Two, c.SerialPortStopBits);
    }

    // ---------- 候选列表本身必须是合法枚举全集 ----------

    [Fact]
    public void 枚举候选列表应包含全部合法值()
    {
        var c = Create();
        Assert.Equal(Enum.GetValues<EnumWorkModel>().Length, c.WorkModelTypes.Length);
        Assert.Equal(Enum.GetValues<EnumAppendContent>().Length, c.AppendContentValues.Length);
        Assert.Equal(Enum.GetValues<EnumEndOfFrame>().Length, c.EndOfFramesValues.Length);
        Assert.Equal(Enum.GetValues<EnumContentType>().Length, c.ContentTypesValues.Length);
        Assert.Equal(Enum.GetValues<Parity>().Length, c.ParityValues.Length);
        Assert.Equal(Enum.GetValues<StopBits>().Length, c.StopBitsValues.Length);
    }
}
