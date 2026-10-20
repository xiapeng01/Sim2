using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace 模拟扫码枪;

public class CommTcpClient:AComm
{
    private TcpClient? _client = null;
    // 保护 _client 的读写：工作循环、发送线程、释放流程会并发访问
    private readonly object _clientLock = new();

    public CommTcpClient()
    {
        _ = Task.Run(DoWork);//被动接收和仅接受模式
        _=Task.Run(CheckConnectedTask);//检查状态线程
    }

    private async Task CheckConnectedTask()
    {
        while (!IsDisposed)
        {
            try
            {
                await Task.Delay(500);//每500毫秒执行一次
                bool status;
                lock (_clientLock)
                {
                    status = _client != null && CheckConnected(_client);
                }
                Post(() => SetIsConnected(status));
            }
            catch (Exception ex)
            {
                UpdateException(ex.Message);
            }
        }
    }

    private async Task DoWork()
    {
        while (!IsDisposed)
        {
            await Task.Delay(10);//间隔10毫秒
            if (IsDisposed) break;
            if (!IsEnabled)
            {
                lock (_clientLock)
                {
                    _client?.Dispose();
                    _client = null;
                }
                SetIsConnected(false);
                await Task.Delay(500);
                continue;
            }
            else
            {
                if (_client is null)
                {
                    // 连接失败（对端未启动/端口不通）必须自己捕获并重试，
                    // 否则异常会冲出 while 循环，工作线程永久退出且没有任何提示。
                    if (!await TryConnectAsync())
                    {
                        await Task.Delay(2000);//失败后退避 2 秒再重试
                        continue;
                    }
                    await Task.Delay(1000);
                }
                else
                {
                    bool alive;
                    lock (_clientLock)
                    {
                        alive = _client is not null && CheckConnected(_client);
                        if (!alive)
                        {
                            _client?.Dispose();
                            _client = null;
                        }
                    }
                    if (!alive) continue;
                }
            }

            try
            {
                switch (WorkModel)
                {
                    case EnumWorkModel.Passive:
                        var s = GetStream();
                        byte[] buffer = new byte[4096];
                        int n = s.Read(buffer); //读取所有内容
                        if (n > 0)
                        {
                            Array.Resize(ref buffer, n);
                            var str = Encoding.UTF8.GetString(buffer);
                            UpdateMessage($"收到内容[{str}]");
                            if (IsVerifyTriggerContent) //通过校验才发送
                            {
                                switch (ReceiveContentType)
                                {
                                    default:
                                    case EnumContentType.Text: //字符串模式不比较空白字符
                                        var clearTriggerString = TriggerContent.Trim();
                                        if (clearTriggerString.Equals(str.Trim()))
                                        {
                                            Send();
                                        }

                                        break;
                                    case EnumContentType.Hex:
                                        var triggerData = HexStringToByteArray(TriggerContent);
                                        if (CompareByteArray(Encoding.UTF8.GetBytes(str), triggerData))
                                        {
                                            Send();
                                        }

                                        break;
                                }
                            }
                            else //不校验,直接发送
                            {
                                Send(); //执行发送内容
                            }
                        }


                        SetIsConnected(true);
                        Debug.WriteLine(DateTime.Now);
                        break;
                    case EnumWorkModel.OnlyReceive:
                        var s2 = GetStream();
                        var buffer2 = new byte[4096];
                        var n2 = s2.Read(buffer2);
                        if (n2 > 0)
                        {
                            Array.Resize(ref buffer2, n2);
                            var str = Encoding.UTF8.GetString(buffer2); //读取所有内容
                            UpdateMessage($"收到内容[{str}]");
                            UpdateReceiveContent(str);
                        }

                        SetIsConnected(true);
                        Debug.WriteLine(DateTime.Now);
                        break;
                }
            }
            catch (IOException)
            {
                
            }
            catch (SocketException)
            {
                
            }
            catch (TimeoutException)
            {
                
            }
            catch (Exception ex)
            {
                Close();
                UpdateException(ex.Message);
            }
        }
    }

    private void Close()
    {
        try
        {
            SetIsConnected(false);
            lock (_clientLock)
            {
                _client?.Dispose();
                _client = null;
            }
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
        }
    }

    /// <summary>
    /// 尝试连接对端。成功返回 true；失败时上报异常并返回 false，由调用方退避后重试。
    /// </summary>
    private async Task<bool> TryConnectAsync()
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            client.ReceiveTimeout = ReadTimeOut;
            client.SendTimeout = WriteTimeOut;
            await client.ConnectAsync(TcpIP, TcpPort);
            lock (_clientLock)
            {
                if (IsDisposed)
                {
                    // 释放流程与连接流程并发，直接丢弃刚建立的连接
                    client.Dispose();
                    return false;
                }
                _client = client;
            }
            return true;
        }
        catch (Exception ex)
        {
            try { client?.Dispose(); } catch { /*忽略*/ }
            lock (_clientLock) { _client = null; }
            UpdateException($"连接 [{TcpIP}:{TcpPort}] 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>释放 Socket 资源</summary>
    protected override void ReleaseResources()
    {
        try
        {
            lock (_clientLock)
            {
                _client?.Dispose();
                _client = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("释放 Socket 失败:" + ex.Message);
        }
    }

    Stream GetStream()
    {
        lock (_clientLock)
        {
            if (_client is null)
            {
                // 连接失败会抛出，由调用方已有 try/catch 处理
                var client = new TcpClient();
                client.ReceiveTimeout = ReadTimeOut;
                client.SendTimeout = WriteTimeOut;
                client.Connect(TcpIP, TcpPort);
                _client = client;
            }
            return _client.GetStream();
        }
    }
    
    public override string Trigger()
    {
        try
        {
            if (!IsEnabled) return"";
            var s = GetStream();

            List<byte> sendDataList = new List<byte>();
            byte[] sendData;
            switch (SendContentType)
            {
                default:
                case EnumContentType.Text:
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(SendContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    s.Write(sendData, 0, sendData.Length); //发送内容
                    break;
                case EnumContentType.Hex:
                    sendDataList.AddRange(HexStringToByteArray(SendContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    s.Write(sendData, 0, sendData.Length); //发送内容
                    break;
            }

            var buffer = new byte[4096];
            for (var i = 0; i < 100; i++)
            {
                var n = s.Read(buffer);
                if (n > 0)
                {
                    Array.Resize(ref buffer,n);
                    var str = Encoding.UTF8.GetString(buffer);
                    UpdateReceiveContent(str);
                    SetIsConnected(true);
                    return str;
                }
            }

            return "";
        }
        catch (Exception ex)
        {
            Close();
            UpdateException(ex.Message);
            return "";
        }
    }

    public override bool Send()
    {
        try
        {
            if (!IsEnabled) return false;
            var s = GetStream();

            List<byte> sendDataList = new List<byte>();
            byte[] sendData;
            switch (SendContentType)
            {
                default:
                case EnumContentType.Text:
                    if(AppendContent != EnumAppendContent.ParseAndSend)sendDataList.AddRange(Encoding.UTF8.GetBytes(SendContent)); //获取发送内容
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetAppendContent())); //附加内容
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    s.Write(sendData, 0, sendData.Length); //发送内容
                    break;
                case EnumContentType.Hex:
                    sendDataList.AddRange(HexStringToByteArray(SendContent)); //获取发送内容
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    s.Write(sendData, 0, sendData.Length); //发送内容
                    break;
            }
            SetIsConnected(true);
            return true;
        }
        catch (Exception ex)
        {
            Close();
            UpdateException(ex.Message);
            return false;
        }
    }
    
    public override bool SendString(string str)
    {
        try
        {
            if (!IsEnabled) return false;
            var sp = GetStream(); 
            List<byte> sendDataList = new List<byte>(); 
            sendDataList.AddRange(Encoding.UTF8.GetBytes(str)); //获取发送内容  
            var sendData = sendDataList.ToArray();
            sp.Write(sendData, 0, sendData.Length); //发送内容 
            SetIsConnected(true);
            return true;
        }
        catch (Exception ex)
        {
            Close();
            UpdateException(ex.Message);
            return false;
        }
    }

    public override bool SendData(byte[] data)
    {
        try
        {
            if (!IsEnabled) return false;
            var sp = GetStream();

            List<byte> sendDataList = new List<byte>(); 
            sp.Write(data, 0, data.Length); //发送内容 
            SetIsConnected(true);
            return true;
        }
        catch (Exception ex)
        {
            Close();
            UpdateException(ex.Message);
            return false;
        }
    }

    public override bool Cancel()
    {
        try
        {
            if (!IsEnabled) return false;
            var s = GetStream();

            List<byte> sendDataList = new List<byte>();
            byte[] sendData;
            switch (SendContentType)
            {
                default:
                case EnumContentType.Text:
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(CancelContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    s.Write(sendData, 0, sendData.Length); //发送内容
                    break;
                case EnumContentType.Hex:
                    sendDataList.AddRange(HexStringToByteArray(CancelContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    s.Write(sendData, 0, sendData.Length); //发送内容
                    break;
            }
            SetIsConnected(true);
            return true;
        }
        catch (Exception ex)
        {
            Close();
            UpdateException(ex.Message);
            return false;
        }
    }
}