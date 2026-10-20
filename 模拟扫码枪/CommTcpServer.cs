using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace 模拟扫码枪;

public class CommTcpServer:AComm
{
    private TcpListener? _server ;
    private readonly List<TcpClient> _clients = new();
    // 保护 _server 的读写：监听线程、工作循环、释放流程会并发访问
    private readonly object _serverLock = new();

    public CommTcpServer()
    {
        _ = Task.Run(DoWork);//被动接收和仅接受模式 
        _ = Task.Run(CheckConnectedTask);//检查状态线程
        _ = Task.Run(ServerWork);//响应客户端连接
        _ = Task.Run(ClearWork);//清理连接状态异常的客户端
    }

    private readonly SemaphoreSlim _sem = new(1,1);
    async Task ClearWork()
    {
        while (!IsDisposed)
        {
            try
            {
                await Task.Delay(100);
                bool anyDead;
                lock (_clients)
                {
                    anyDead = _clients.Any(c => !CheckConnected(c));
                }
                if (!anyDead) continue;

                try
                {
                    await _sem.WaitAsync();
                    lock (_clients)
                    {
                        _clients.RemoveAll(a => !CheckConnected(a));
                    }
                }
                catch (Exception ex)
                {
                    UpdateException(ex.Message);
                }
                finally
                {
                    _sem.Release();
                }

            }
            catch (Exception ex)
            {
                UpdateException(ex.Message);
            }
        }
    }

    private async Task CheckConnectedTask()
    {
        while (!IsDisposed)
        {
            try
            {
                await Task.Delay(500);//每500毫秒执行一次
                bool status;
                int count;
                lock (_clients)
                {
                    status = _clients.Any(CheckConnected);
                    count = _clients.Count;
                }
                Post(() => SetIsConnected(status));
                Post(() => { ClientCount = count; });
            }
            catch (Exception ex)
            {
                UpdateException(ex.Message);
            }
        }
    }

    async Task ServerWork()
    {
        while (!IsDisposed)
        {
            try
            {
                await Task.Delay(50);
                TcpListener? server;
                lock (_serverLock)
                {
                    server = _server;
                }
                if (server is null) continue;
                var client = await server.AcceptTcpClientAsync();
                _ = Task.Run(() => DoClientWork(client));
                lock (_clients)
                {
                    _clients.Add(client);
                }
            }
            catch (ObjectDisposedException)
            {
                // 监听已被停止，属于正常退出路径
            }
            catch (InvalidOperationException)
            {
                // 监听已被停止，属于正常退出路径
            }
            catch (SocketException ex)
            {
                UpdateException($"接受客户端连接失败：{ex.Message}");
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                UpdateException(ex.Message);
                await Task.Delay(1000);
            }

        }
    }

    private async Task DoWork()
    {
        while (!IsDisposed)
        {
            await Task.Delay(10);//间隔10毫秒
            if (IsDisposed) break;
            try
            {
                if (!IsEnabled)
                {
                    lock (_clients)
                    {
                        foreach (var client in _clients)
                        {
                            client.Dispose();
                        }
                        _clients.Clear();
                    }
                    StopServer();
                    SetIsConnected(false);
                    await Task.Delay(500);
                    continue;
                }

                bool needStart;
                lock (_serverLock)
                {
                    needStart = _server == null;
                }
                if (needStart)
                {
                    // 监听端口可能被占用（Address already in use）。
                    // 必须捕获并上报，且失败后把 _server 复位，否则永远不会重试。
                    if (!TryStartServer())
                    {
                        await Task.Delay(2000);//失败后退避 2 秒再重试
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateException(ex.Message);
                await Task.Delay(1000);
            }
        }
    }

    /// <summary>
    /// 启动监听。成功返回 true；失败时上报异常并复位 _server，由调用方退避后重试。
    /// </summary>
    private bool TryStartServer()
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, TcpPort);
            listener.Start();
            lock (_serverLock)
            {
                _server = listener;
            }
            return true;
        }
        catch (Exception ex)
        {
            try { listener?.Stop(); } catch { /*忽略*/ }
            lock (_serverLock) { _server = null; }
            UpdateException($"启动 TCP 监听端口 [{TcpPort}] 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>停止并释放监听端口</summary>
    private void StopServer()
    {
        TcpListener? server;
        lock (_serverLock)
        {
            server = _server;
            _server = null;
        }
        try
        {
            server?.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("停止监听失败:" + ex.Message);
        }
    }

    async Task DoClientWork(TcpClient? client)
    {
        while(!IsDisposed &&(client !=null) && CheckConnected(client))
        {
            try
            {
                await Task.Delay(5);
                switch (WorkModel)
                {
                    case EnumWorkModel.Passive:
                        var s = client.GetStream();
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
                                            Send(client);
                                        }

                                        break;
                                    case EnumContentType.Hex:
                                        var triggerData = HexStringToByteArray(TriggerContent);
                                        if (CompareByteArray(Encoding.UTF8.GetBytes(str), triggerData))
                                        {
                                            Send(client);
                                        }

                                        break;
                                }
                            }
                            else //不校验,直接发送
                            {
                                Send(client); //执行发送内容
                            }
                        }


                        SetIsConnected(true);
                        Debug.WriteLine(DateTime.Now);
                        break;
                    case EnumWorkModel.OnlyReceive:
                        var s2 = client.GetStream();
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
                client.Dispose();
                UpdateException(ex.Message);
            }
        }
    }

    private void Close()
    {
        try
        {
            SetIsConnected(false);
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }
                _clients.Clear();
            }
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
        }
    }

    /// <summary>释放监听端口与所有客户端连接</summary>
    protected override void ReleaseResources()
    {
        try
        {
            StopServer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("停止监听失败:" + ex.Message);
        }
        try
        {
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }
                _clients.Clear();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("释放客户端失败:" + ex.Message);
        }
    }

    Stream GetStream()
    {
        try
        {
            _sem.Wait();
            TcpClient? client;
            lock (_clients)
            {
                client = _clients.FirstOrDefault(CheckConnected);
            }
            if (client is null) throw new Exception("没有已连接的客户端");
            client.ReceiveTimeout = ReadTimeOut;
            client.SendTimeout = WriteTimeOut;
            return client.GetStream();

        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
            throw;
        }
        finally
        {
            _sem.Release();
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

    public bool Send(TcpClient client)
    {
        return Send(client.GetStream());
    }
    
    public bool Send(Stream s)
    {
        try
        {
            if (!IsEnabled) return false; 
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