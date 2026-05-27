using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Asn1;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace 模拟扫码枪;

public enum EnumInterfaceType{SerialPort,TcpClient,TcpServer}
public enum EnumWorkModel{Trigger,Passive,OnlySend,OnlyReceive}
public enum EnumAppendContent{None,Random,OrderNumber,ParseAndSend }//不附加内容,随机数,序列号,解析发送内容并附加在发送内容后面-仅发送内容是十六进制字符串模式用,解析发送内容中的十六进制字符串并附加在发送内容后面 
public enum EnumEndOfFrame{None,CR,LR,CRLR}
public enum EnumContentType{Text,Hex}

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

    public EnumInterfaceType[] InterfaceTypeValues => Enum.GetValues<EnumInterfaceType>();
    public EnumWorkModel[] WorkModelTypes => Enum.GetValues<EnumWorkModel>();
    public EnumAppendContent[] AppendContentValues => Enum.GetValues<EnumAppendContent>();
    public EnumEndOfFrame[] EndOfFramesValues => Enum.GetValues<EnumEndOfFrame>();
    public EnumContentType[] ContentTypesValues => Enum.GetValues<EnumContentType>();

    public string[] SerialPortNameValues => new List<string>(new string[] { ""}).Concat(SerialPort.GetPortNames().OrderBy(x => x)).ToArray();
    public Parity[] ParityValues => Enum.GetValues<Parity>();
    public StopBits[] StopBitsValues => Enum.GetValues<StopBits>();

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

    private string _name="未命名";
    protected AComm()
    {
        _sendCommand = new AsyncRelayCommand(HandleSendCommand,()=>IsEnabled && (WorkModel == EnumWorkModel.Trigger || WorkModel==EnumWorkModel.OnlySend)); 
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
            if (value == _serialPortName) return;
            _serialPortName = value;
            OnPropertyChanged();
        }
    }

    public int SerialPortBaudRate
    {
        get => _serialPortBaudRate;
        set
        {
            if (value == _serialPortBaudRate) return;
            _serialPortBaudRate = value;
            OnPropertyChanged();
        }
    }

    public int SerialPortDataBits
    {
        get => _serialPortDataBits;
        set
        {
            if (value == _serialPortDataBits) return;
            _serialPortDataBits = value;
            OnPropertyChanged();
        }
    }

    public Parity SerialPortParity
    {
        get => _serialPortParity;
        set
        {
            if (value == _serialPortParity) return;
            _serialPortParity = value;
            OnPropertyChanged();
        }
    }

    public StopBits SerialPortStopBits
    {
        get => _serialPortStopBits;
        set
        {
            if (value == _serialPortStopBits) return;
            _serialPortStopBits = value;
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
            if (value == _workModel) return;
            _workModel = value;
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
            if (value == _appendContent) return;
            _appendContent = value;
            OnPropertyChanged();
        }
    }

    public EnumEndOfFrame EndOfFrame
    {
        get => _endOfFrame;
        set
        {
            if (value == _endOfFrame) return;
            _endOfFrame = value;
            OnPropertyChanged();
        }
    }

    public EnumContentType SendContentType
    {
        get => _sendContentType;
        set
        {
            if (value == _sendContentType) return;
            _sendContentType = value;
            OnPropertyChanged();
        }
    }

    public EnumContentType ReceiveContentType
    {
        get => _receiveContentType;
        set
        {
            if (value == _receiveContentType) return;
            _receiveContentType = value;
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

    public void Dispose()
    {
        IsEnabled = false;
        IsDisposed = true;
    }
    
    protected void UpdateReceiveContent(string content,[CallerMemberName] string name="")
    {
        Debug.WriteLine($"{name}:{content}");
        Application.Current?.Dispatcher?.Invoke(() => ReceiveContent = content);
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
        Application.Current?.Dispatcher?.Invoke(() => IsConnected = isConnected);
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
            Application.Current?.Dispatcher?.Invoke(act);
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

public partial class ScannerWrap:ObservableObject,IDisposable
{
    [ObservableProperty] private AComm _scanner = new CommSerialPort();
    [ObservableProperty] EnumInterfaceType _interfaceType= EnumInterfaceType.SerialPort;

    
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is null) return;
        if (e.PropertyName.Equals(nameof(InterfaceType)))
        {
            Scanner.IsEnabled = false;
            Scanner.Dispose();
            Scanner = CreateObject(InterfaceType); 
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


    bool _isDisposed;

    public void Dispose()
    {
        Scanner?.Dispose();
        _isDisposed = true;
    }
}