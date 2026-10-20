using System.ComponentModel;
using System.Diagnostics; 
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;

namespace 模拟扫码枪;

public enum EnumInterfaceType
{
    [Description("串口")] SerialPort,
    [Description("TCP 客户端")] TcpClient,
    [Description("TCP 服务端")] TcpServer
}
public enum EnumWorkModel
{
    [Description("主动触发")] Trigger,
    [Description("被动接收")] Passive,
    [Description("仅发送")] OnlySend,
    [Description("仅接收")] OnlyReceive
}
public enum EnumAppendContent
{
    [Description("不附加")] None,
    [Description("随机数")] Random,
    [Description("序列号")] OrderNumber,
    [Description("解析发送内容并附加")] ParseAndSend //解析发送内容并附加在发送内容后面-仅发送内容是十六进制字符串模式用,解析发送内容中的十六进制字符串并附加在发送内容后面
}
public enum EnumEndOfFrame
{
    [Description("无")] None,
    [Description("CR (\\r)")] CR,
    [Description("LF (\\n)")] LR,
    [Description("CRLF (\\r\\n)")] CRLR
}
public enum EnumContentType
{
    [Description("文本")] Text,
    [Description("HEX 十六进制")] Hex
}

public interface IComm
{
    event Action<string>? UpdateReceiveContentEvent;
    event Action<string>? UpdateMessageEvent;
    event Action<string>? UpdateExceptionEvent;
    string Name { get; set; }
     EnumInterfaceType[] InterfaceTypeValues{ get; }
     EnumWorkModel[] WorkModelTypes { get; }
    EnumAppendContent[] AppendContentValues { get; }
    EnumEndOfFrame[] EndOfFramesValues { get; }
    EnumContentType[] ContentTypesValues { get;  }
    string[] SerialPortNameValues { get; }
    Parity[] ParityValues { get; }
    StopBits[] StopBitsValues { get;  }

    IRelayCommand SendCommand { get; set; }
    IRelayCommand CancelCommand { get; set; }
    bool IsEnabled { get; set; }//是否启用//是否启用
    bool IsVerifyTriggerContent { get; set; }//校验触发内容-仅被动模式用
    string SerialPortName { get; set; }
    int SerialPortBaudRate { get; set;}
    int SerialPortDataBits { get; set; }
    Parity SerialPortParity { get; set;}
    StopBits SerialPortStopBits { get; set; }
    string TcpIP { get; set; }
    int TcpPort { get; set;}
    bool IsConnected { get; }//已连接状态
    EnumInterfaceType InterfaceType { get; set; }//接口类型
    EnumWorkModel WorkModel { get; set; }//工作模式
    int AppendContentLength { get; set; }//附加内容长度
    EnumAppendContent AppendContent { get; set; }//附加内容
    EnumEndOfFrame EndOfFrame { get; set; }//发送内容附加结束符
    EnumContentType SendContentType { get; set; }//发送内容类型
    EnumContentType ReceiveContentType { get; set; }//接受内容类型
    string TriggerContent { get; set;}//触发内容-被动模式用,接受内容符合触发内容格式就算触发
    string CancelContent { get; set; }//取消内容
    string SendContent{ get; set;}//主动模式和仅发送模式用
    string ReceiveContent { get; set; }//接受内容
    int SendInterval { get; set; }//仅发送的时间间隔
    int ClientCount { get; set; }//客户端连接数量
    int WriteTimeOut { get; set; }
    int ReadTimeOut { get; set; }
    int OrderNumber { get; set; }//序列号-仅附加内容是序列号用
    string Trigger();
    Task<string> TriggerAsync();//异步触发,被动模式用,通过校验才算触发
    bool Send();
    bool SendString(string str);
    bool SendData(byte[] data);
    bool Cancel();
}

//抽象类不能是分部类,此处不能继承自ObservableObject
public abstract class AComm : IComm, INotifyPropertyChanged, IDisposable
{
    public event Action<string>? UpdateReceiveContentEvent;
    public event Action<string>? UpdateMessageEvent;
    public event Action<string>? UpdateExceptionEvent;

    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            _name = value;
            OnPropertyChanged();
        }
    }
    [JsonIgnore]
    public EnumInterfaceType[] InterfaceTypeValues => Enum.GetValues<EnumInterfaceType>();
    [JsonIgnore]
    public EnumWorkModel[] WorkModelTypes => Enum.GetValues<EnumWorkModel>();
    [JsonIgnore]
    public EnumAppendContent[] AppendContentValues => Enum.GetValues<EnumAppendContent>();
    [JsonIgnore]
    public EnumEndOfFrame[] EndOfFramesValues => Enum.GetValues<EnumEndOfFrame>();
    [JsonIgnore]
    public EnumContentType[] ContentTypesValues => Enum.GetValues<EnumContentType>();
    [JsonIgnore]
    public Parity[] ParityValues => Enum.GetValues<Parity>();
    [JsonIgnore]
    public StopBits[] StopBitsValues => Enum.GetValues<StopBits>();

    /// <summary>
    /// 防御性校验：<c>setting.json</c> 被手工编辑或来自旧版本时，可能存有越界的枚举数字。
    /// 非法值会让 ComboBox 无法匹配任何选项而显示空白，因此统一回退到第一个合法值。
    /// </summary>
    static T NormalizeEnum<T>(T value) where T : struct, Enum
        => Enum.IsDefined(typeof(T), value) ? value : Enum.GetValues<T>()[0];

    /// <summary>
    /// 串口号候选列表。
    /// <para>
    /// 必须把当前 <see cref="SerialPortName"/> 也并入列表：若配置里的串口当前不存在
    /// （拔掉了、换了机器、虚拟串口未启动），ComboBox 的 SelectedItem 绑定会失败并被置为 null，
    /// 界面上就会显示空白，用户误以为配置丢失。
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string[] SerialPortNameValues
    {
        get
        {
            var list = new List<string> { "" };
            list.AddRange(SerialPort.GetPortNames().OrderBy(x => x));
            // 追加当前值（去重），保证一定能选中显示
            if (!string.IsNullOrEmpty(SerialPortName) && !list.Contains(SerialPortName))
            {
                list.Add(SerialPortName);
            }
            return list.ToArray();
        }
    }

    /// <summary>
    /// 波特率候选列表。同样合并当前值，避免配置中的非标准波特率显示为空白。
    /// </summary>
    [JsonIgnore]
    public int[] SerialPortBaudRateValues
    {
        get
        {
            var list = new List<int> { 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
            if (!list.Contains(SerialPortBaudRate)) list.Add(SerialPortBaudRate);
            return list.ToArray();
        }
    }

    /// <summary>数据位候选列表，同样合并当前值。</summary>
    [JsonIgnore]
    public int[] SerialPortDataBitsValues
    {
        get
        {
            var list = new List<int> { 7, 8 };
            if (!list.Contains(SerialPortDataBits)) list.Add(SerialPortDataBits);
            return list.ToArray();
        }
    }

    [JsonIgnore]
    public IRelayCommand SendCommand
    {
        get => _sendCommand;
        set
        {
            if (Equals(value, _sendCommand)) return;
            _sendCommand = value;
            OnPropertyChanged();
        }
    }

    IRelayCommand _cancelCommand;
    [JsonIgnore]
    public IRelayCommand CancelCommand
    {
        get => _cancelCommand;
        set
        {
            if (Equals(value, _cancelCommand)) return;
            _cancelCommand = value;
            OnPropertyChanged();
        }
    }

    private bool _isEnabled;
    
    bool _isConnected;
    private string _serialPortName="COM1";
    private int _serialPortBaudRate=115200;
    private int _serialPortDataBits=8;
    private Parity _serialPortParity=Parity.None;
    private StopBits _serialPortStopBits=StopBits.One;
    private string _tcpIp="127.0.0.1";
    private int _tcpPort=8000;
    private EnumInterfaceType _interfaceType = EnumInterfaceType.SerialPort;
    private EnumWorkModel _workModel= EnumWorkModel.Trigger;
    private int _appendContentLength =6;
    private EnumAppendContent _appendContent = EnumAppendContent.None;
    private EnumEndOfFrame _endOfFrame= EnumEndOfFrame.None;
    private EnumContentType _sendContentType = EnumContentType.Text;
    private EnumContentType _receiveContentType = EnumContentType.Text;
    private string _triggerContent="16540D";
    private string _cancelContent="16550D";
    private string _sendContent = "SendString";
    private string _receiveContent = "";
    private int _sendInterval ;
    private int _clientCount;
    
    protected bool IsDisposed;
    private int _sendTimeOut=500;
    private int _receiveTimeOut=500;
    private bool _isVerifyTriggerContent=true;
    private IRelayCommand _sendCommand ;

    private string _name = "未命名";
    protected AComm()
    {
        _sendCommand = new AsyncRelayCommand(HandleSendCommand, () => IsEnabled && (WorkModel == EnumWorkModel.Trigger || WorkModel == EnumWorkModel.OnlySend));
        _cancelCommand = new AsyncRelayCommand(HandleCancelCommand, () => IsEnabled && (WorkModel == EnumWorkModel.Trigger || WorkModel == EnumWorkModel.OnlySend));
    }

    private async Task HandleSendCommand()
    {
        switch (WorkModel)
        {
            case EnumWorkModel.Trigger:
                await TriggerAsync();
                break;
            case EnumWorkModel.OnlySend:
                if (SendInterval == 0)
                {
                    Send();
                }
                else
                {
                    while (IsEnabled && !IsDisposed)
                    {
                        Send();
                        await Task.Delay(SendInterval);
                    }
                }
                break;
        }
    }

    private async Task HandleCancelCommand()
    {
        Cancel();
    }

    public bool IsEnabled 
    { 
        get=>_isEnabled;
        set { _isEnabled = value;OnPropertyChanged(); }
    }

    public bool IsVerifyTriggerContent
    {
        get => _isVerifyTriggerContent;
        set
        {
            if (value == _isVerifyTriggerContent) return;
            _isVerifyTriggerContent = value;
            OnPropertyChanged();
        }
    }

    public string SerialPortName
    {
        get => _serialPortName;
        set
        {
            // ComboBox 的 SelectedItem 在 ItemsSource 变化后可能被置为 null，
            // 而 SelectedItem 是双向绑定的，会把 null 反向写回源导致配置丢失。
            // 这里拒绝 null，保留原值；候选列表已保证原值始终可选，界面不会显示空白。
            if (value is null) return;
            if (value == _serialPortName) return;
            _serialPortName = value;
            OnPropertyChanged();
            // 候选列表依赖当前值：新值若不在旧列表中，下拉框会匹配不到而显示空白
            OnPropertyChanged(nameof(SerialPortNameValues));
        }
    }

    public int SerialPortBaudRate
    {
        get => _serialPortBaudRate;
        set
        {
            // 非法值（<=0）会匹配不到任何选项，这里直接忽略，保持原值
            if (value <= 0) return;
            if (value == _serialPortBaudRate) return;
            _serialPortBaudRate = value;
            OnPropertyChanged();
            // 候选列表依赖当前值：新值若不在旧列表中，下拉框会匹配不到而显示空白
            OnPropertyChanged(nameof(SerialPortBaudRateValues));
        }
    }

    public int SerialPortDataBits
    {
        get => _serialPortDataBits;
        set
        {
            // 数据位只有 7 / 8 两种合法值
            if (value != 7 && value != 8) return;
            if (value == _serialPortDataBits) return;
            _serialPortDataBits = value;
            OnPropertyChanged();
            // 候选列表依赖当前值：新值若不在旧列表中，下拉框会匹配不到而显示空白
            OnPropertyChanged(nameof(SerialPortDataBitsValues));
        }
    }

    public Parity SerialPortParity
    {
        get => _serialPortParity;
        set
        {
            // 归一化：非法枚举值会导致 ComboBox 匹配不到任何项而显示空白
            var v = NormalizeEnum(value);
            if (v == _serialPortParity) return;
            _serialPortParity = v;
            OnPropertyChanged();
        }
    }

    public StopBits SerialPortStopBits
    {
        get => _serialPortStopBits;
        set
        {
            // 归一化：非法枚举值会导致 ComboBox 匹配不到任何项而显示空白
            var v = NormalizeEnum(value);
            if (v == _serialPortStopBits) return;
            _serialPortStopBits = v;
            OnPropertyChanged();
        }
    }

    public string TcpIP
    {
        get => _tcpIp;
        set
        {
            if (value == _tcpIp) return;
            _tcpIp = value;
            OnPropertyChanged();
        }
    }

    public int TcpPort
    {
        get => _tcpPort;
        set
        {
            if (value == _tcpPort) return;
            _tcpPort = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsConnected 
    { 
        get=>_isConnected;
        private set { _isConnected = value;OnPropertyChanged(); }
    }

    public EnumInterfaceType InterfaceType
    {
        get => _interfaceType;
        set
        {
            if (value == _interfaceType) return;
            _interfaceType = value;
            OnPropertyChanged();
        }
    }

    public EnumWorkModel WorkModel
    {
        get => _workModel;
        set
        {
            // 归一化：非法枚举值会导致 ComboBox 匹配不到任何项而显示空白
            var v = NormalizeEnum(value);
            if (v == _workModel) return;
            _workModel = v;
            OnPropertyChanged();
        }
    }

    public int AppendContentLength
    {
        get => _appendContentLength;
        set
        {
            if (value == _appendContentLength) return;
            _appendContentLength = value;
            OnPropertyChanged();
        }
    }

    public EnumAppendContent AppendContent
    {
        get => _appendContent;
        set
        {
            // 归一化：非法枚举值会导致 ComboBox 匹配不到任何项而显示空白
            var v = NormalizeEnum(value);
            if (v == _appendContent) return;
            _appendContent = v;
            OnPropertyChanged();
        }
    }

    public EnumEndOfFrame EndOfFrame
    {
        get => _endOfFrame;
        set
        {
            // 归一化：非法枚举值会导致 ComboBox 匹配不到任何项而显示空白
            var v = NormalizeEnum(value);
            if (v == _endOfFrame) return;
            _endOfFrame = v;
            OnPropertyChanged();
        }
    }

    public EnumContentType SendContentType
    {
        get => _sendContentType;
        set
        {
            // 归一化：非法枚举值会导致 ComboBox 匹配不到任何项而显示空白
            var v = NormalizeEnum(value);
            if (v == _sendContentType) return;
            _sendContentType = v;
            OnPropertyChanged();
        }
    }

    public EnumContentType ReceiveContentType
    {
        get => _receiveContentType;
        set
        {
            // 归一化：非法枚举值会导致 ComboBox 匹配不到任何项而显示空白
            var v = NormalizeEnum(value);
            if (v == _receiveContentType) return;
            _receiveContentType = v;
            OnPropertyChanged();
        }
    }

    public string TriggerContent
    {
        get => _triggerContent;
        set
        {
            if (value == _triggerContent) return;
            _triggerContent = value;
            OnPropertyChanged();
        }
    }

    public string CancelContent
    {
        get => _cancelContent;
        set
        {
            if (value == _cancelContent) return;
            _cancelContent = value;
            OnPropertyChanged();
        }
    }

    public string SendContent
    {
        get => _sendContent;
        set
        {
            if (value == _sendContent) return;
            _sendContent = value;
            OnPropertyChanged();
        }
    }

    public string ReceiveContent
    {
        get => _receiveContent;
        set
        {
            if (value == _receiveContent) return;
            _receiveContent = value;
            OnPropertyChanged();
        }
    }

    public int SendInterval
    {
        get => _sendInterval;
        set
        {
            if (value == _sendInterval) return;
            _sendInterval = value;
            OnPropertyChanged();
        }
    }

    public int ClientCount
    {
        get => _clientCount;
        set
        {
            if (value == _clientCount) return;
            _clientCount = value;
            OnPropertyChanged();
        }
    }

    public int WriteTimeOut
    {
        get => _sendTimeOut;
        set
        {
            if (value == _sendTimeOut) return;
            _sendTimeOut = value;
            OnPropertyChanged();
        }
    }

    public int ReadTimeOut
    {
        get => _receiveTimeOut;
        set
        {
            if (value == _receiveTimeOut) return;
            _receiveTimeOut = value;
            OnPropertyChanged();
        }
    }

    public int OrderNumber
    {
        get => _orderNumber;
        set
        {
            if (value == _orderNumber) return;
            _orderNumber = value;
            OnPropertyChanged();
        }
    }
     

    private int _orderNumber;

    public abstract string Trigger();

    public async Task<string> TriggerAsync()
    {
        return await Task.Run(() => Trigger());
    }
    public abstract bool Send();
    public abstract bool SendString(string str);
    public abstract bool SendData(byte[] data);
    public abstract bool Cancel();
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName != null)
        {
            if (propertyName.Equals(nameof(IsEnabled)) || propertyName.Equals(nameof(WorkModel)))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 释放底层通信资源。子类必须重写，否则串口句柄 / Socket / 监听端口会一直被占用。
    /// </summary>
    protected virtual void ReleaseResources()
    {
    }

    public virtual void Dispose()
    {
        if (IsDisposed) return;
        IsEnabled = false;
        IsDisposed = true;
        try
        {
            ReleaseResources();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("释放资源失败:" + ex.Message);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 把当前配置复制到另一个通信对象上。切换接口类型时会重建底层通信对象，
    /// 若不复制则用户已填写的 IP/端口/内容等参数会全部丢失。
    /// </summary>
    public void CopySettingsTo(AComm target)
    {
        if (target is null) return;
        target.Name = Name;
        target.IsVerifyTriggerContent = IsVerifyTriggerContent;
        target.SerialPortName = SerialPortName;
        target.SerialPortBaudRate = SerialPortBaudRate;
        target.SerialPortDataBits = SerialPortDataBits;
        target.SerialPortParity = SerialPortParity;
        target.SerialPortStopBits = SerialPortStopBits;
        target.TcpIP = TcpIP;
        target.TcpPort = TcpPort;
        target.WorkModel = WorkModel;
        target.AppendContentLength = AppendContentLength;
        target.AppendContent = AppendContent;
        target.EndOfFrame = EndOfFrame;
        target.SendContentType = SendContentType;
        target.ReceiveContentType = ReceiveContentType;
        target.TriggerContent = TriggerContent;
        target.CancelContent = CancelContent;
        target.SendContent = SendContent;
        target.ReceiveContent = ReceiveContent;
        target.SendInterval = SendInterval;
        target.WriteTimeOut = WriteTimeOut;
        target.ReadTimeOut = ReadTimeOut;
        target.OrderNumber = OrderNumber;
    }

    protected void UpdateReceiveContent(string content,[CallerMemberName] string name="")
    {
        Debug.WriteLine($"{name}:{content}");
        Post(() => ReceiveContent = content);
        UpdateReceiveContentEvent?.Invoke(content);
    }
    
    protected void UpdateMessage(string content,[CallerMemberName] string name="")
    {
        Debug.WriteLine($"{name}:{content}");
        UpdateMessageEvent?.Invoke(content);
    }
    
    protected void UpdateException(string content,[CallerMemberName] string name="")
    {
        Debug.WriteLine($"{name}:{content}");
        UpdateExceptionEvent?.Invoke(content);
    }
    
    //十六进制字符串转字节数组
    protected byte[] HexStringToByteArray(string str)
    {
        string t = str.Replace(" ", "").ToUpper();
        if (t.Length <= 0) throw new Exception("内容为空");
        if (t.Length % 2 != 0) t = t + "0";//在末尾增加一个0

        var ret = Enumerable.Range(0, t.Length)
            .Where(x => x % 2 == 0)
            .Select(y => Convert.ToByte(t.Substring(y, 2), 16))
            .ToArray();
        return ret;
    }

    protected bool CompareByteArray(byte[] b1, byte[] b2)
    {
        if (b1.Length == 0 || b2.Length == 0) return false;
        if (b1.Length != b2.Length) return false;
        return !b1.Where((t, i) => t != b2[i]).Any();
    }

    protected void SetIsConnected(bool isConnected)
    {
        Post(() => IsConnected = isConnected);
    }

    private readonly Random _rd = new Random(Guid.NewGuid().GetHashCode());
    

    protected string GetAppendContent()
    {
        switch(AppendContent)
        {
            default:
            case EnumAppendContent.None:
                return "";
            case EnumAppendContent.Random:
                var str = _rd.Next(0, Int32.MaxValue).ToString().PadLeft(10, '0');
                return str.Substring(str.Length - AppendContentLength, AppendContentLength);
            case EnumAppendContent.OrderNumber:
                var str2 = (OrderNumber++).ToString().PadLeft(10,'0');
                return str2.Substring(str2.Length - AppendContentLength, AppendContentLength);
            case EnumAppendContent.ParseAndSend:
                var sendList = SendContent.Split("|");//解析发送内容,每个内容之间用|分隔                
                return sendList[_rd.Next(0,sendList.Length)];
        } 
    }

    protected string GetEOF()
    {
        switch(EndOfFrame)
        {
            default:
            case EnumEndOfFrame.None:
                return "";
            case EnumEndOfFrame.CR:
                return "\r";
            case EnumEndOfFrame.LR:
                return "\n";
            case EnumEndOfFrame.CRLR:
                return "\r\n";
        }
    }
    
    protected bool Post(Action act)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                // 无 UI 线程（例如单元测试）时直接执行
                act();
                return true;
            }
            if (dispatcher.CheckAccess())
            {
                act();
            }
            else
            {
                // 用 BeginInvoke 而非 Invoke：Invoke 会同步阻塞通信线程，
                // 界面弹出模态窗口时会把所有通信线程一起卡住。
                dispatcher.BeginInvoke(act);
            }
            return true;
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
            return false;
        }
    }
    
    protected bool CheckConnected(Socket? s)
    {
        return s != null && !(s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) && s.Connected;
    }

    protected bool CheckConnected(TcpClient? client)
    {
        return client != null && CheckConnected(client.Client);
    }
}

public partial class ScannerWrap : ObservableObject, IDisposable
{
    [ObservableProperty] private EnumInterfaceType _interfaceType = EnumInterfaceType.SerialPort;
    [ObservableProperty] private AComm _scanner = new CommSerialPort();

    /// <summary>接口类型变化时触发，便于外部重新挂接事件</summary>
    public event EventHandler? ScannerReplaced;

    public ScannerWrap()
    {
        _ = Task.Run(CheckScannerIsEnabled);
    }

    async Task CheckScannerIsEnabled()
    {
        while (!_isDisposed)
        {
            try
            {
                await Task.Delay(50);
                // 注意：这里不能调用 Scanner.Dispose()。
                // Dispose 会把 IsDisposed 置为 true，导致通信类内部的工作循环永久退出，
                // 之后重新勾选"启用"也不会再工作。
                // 各通信类自身的循环已经会在 IsEnabled == false 时释放端口/连接并可再次恢复。
            }
            catch
            {
                // ignored
            }
        }
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(InterfaceType))
        {
            // 只有当底层对象类型与所选接口不一致时才重建。
            // 这样反序列化 setting.json 时（无论属性写入顺序如何）都能保留已保存的实例与参数。
            if (!MatchesInterface(Scanner, InterfaceType))
            {
                var old = Scanner;
                var created = CreateObject(InterfaceType);
                // 切换接口类型时保留用户已填写的参数
                old?.CopySettingsTo(created);
                // 旧实例由 OnScannerChanged 统一释放
                Scanner = created;
                ScannerReplaced?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // 类型一致时仅同步枚举值（例如配置文件中外层与内层不一致的情况）
                Scanner.InterfaceType = InterfaceType;
            }
        }
        else if (e.PropertyName is nameof(Scanner))
        {
            // 保证内层对象的接口类型与外层一致（配置文件中可能不一致）
            if (Scanner is not null && Scanner.InterfaceType != InterfaceType)
            {
                Scanner.InterfaceType = InterfaceType;
            }
        }
    }

    /// <summary>判断底层对象是否已是对应接口类型的实例</summary>
    static bool MatchesInterface(AComm? comm, EnumInterfaceType type)
    {
        return type switch
        {
            EnumInterfaceType.SerialPort => comm is CommSerialPort,
            EnumInterfaceType.TcpClient => comm is CommTcpClient,
            EnumInterfaceType.TcpServer => comm is CommTcpServer,
            _ => false
        };
    }

    /// <summary>底层通信对象被替换时，释放旧实例避免后台线程泄漏</summary>
    partial void OnScannerChanged(AComm? oldValue, AComm newValue)
    {
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue))
        {
            oldValue.Dispose();
        }
    }

    AComm CreateObject(EnumInterfaceType type)
    {
        switch (type)
        {
            case EnumInterfaceType.SerialPort:
                return new CommSerialPort();
            case EnumInterfaceType.TcpClient:
                return new CommTcpClient();
            case EnumInterfaceType.TcpServer:
                return new CommTcpServer();

        }
        throw new Exception("名称不正确");
    }

    protected bool IsSocketConnected(TcpClient client)
    {
        return client != null && !(client.Client.Poll(1000, SelectMode.SelectRead) && (client.Available == 0)) && client.Connected;
    }

    protected bool IsSocketConnected(Socket client)
    {
        if (client == null || client == null) return false;

        // 先检查基础状态
        if (!client.Connected) return false;

        try
        {
            // 检查底层 Socket 是否有可读状态，且可读数据量为 0（表示对端已关闭）
            // 1000 为微秒级超时时间
            bool poll = client.Poll(1000, SelectMode.SelectRead);
            if (poll && client.Available == 0)
            {
                return false; // 连接已断开
            }
            return true; // 连接正常或有数据待读取
        }
        catch (SocketException)
        {
            return false;
        }
    }


    bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            Scanner?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("释放模拟器失败:" + ex.Message);
        }
    }
}