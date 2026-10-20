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

                bool acquired = false;
                try
                {
                    acquired = await _sem.WaitAsync(TimeSpan.FromSeconds(2));
                    if (!acquired)
                    {
                        UpdateException("清理客户端超时");
                        continue;
                    }
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
                    // 只有真正获取到信号量才释放，否则计数会被抬高，
                    // 导致 GetStream 的互斥保护失效
                    if (acquired) _sem.Release();
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
                    CloseAllClients();
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
        if (client is null) return;

        // 关键：必须给客户端设置收发超时。
        // 原实现直接 client.GetStream()，绕过了下面 GetStream() 里设置超时的逻辑，
        // 导致 s.Read() 无限阻塞 —— 每个连上来但不发数据的客户端都会永久占住一个线程池线程，
        // 客户端一多就会耗尽线程池导致界面卡死。
        client.ReceiveTimeout = ReadTimeOut;
        client.SendTimeout = WriteTimeOut;

        while(!IsDisposed && CheckConnected(client))
        {
            try
            {
                await Task.Delay(5);
                switch (WorkModel)
                {
                    case EnumWorkModel.Passive:
                        var s = client.GetStream();
                        byte[] buffer = new byte[4096];
                        int n = ReadWithTimeout(s, buffer); //读取所有内容（带超时）
                        if (n > 0)
                        {
                            var raw = new byte[n];
                            Array.Copy(buffer, raw, n);
                            var str = Encoding.UTF8.GetString(raw);
                            UpdateMessage($"收到内容[{str}]");
                            // 触发匹配统一走基类：HEX 模式用原始字节比较，
                            // 不能用「解码成字符串再编码回来」（对非 UTF-8 合法字节有损）
                            if (IsTriggerMatched(raw, str))
                            {
                                Send(client);
                            }
                        }


                        SetIsConnected(true);
                        TraceDebug(DateTime.Now.ToString());
                        break;
                    case EnumWorkModel.OnlyReceive:
                        var s2 = client.GetStream();
                        var buffer2 = new byte[4096];
                        var n2 = ReadWithTimeout(s2, buffer2);
                        if (n2 > 0)
                        {
                            Array.Resize(ref buffer2, n2);
                            var str = Encoding.UTF8.GetString(buffer2); //读取所有内容
                            UpdateMessage($"收到内容[{str}]");
                            UpdateReceiveContent(str);
                        }

                        SetIsConnected(true);
                        TraceDebug(DateTime.Now.ToString());
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

    /// <summary>
    /// 带超时的读取。超时返回 0（表示本次没有数据），不抛异常、不阻塞工作线程。
    /// <para>
    /// 必须有超时：否则每个「连上但不发数据」的客户端都会永久占住一个线程池线程，
    /// 客户端数量一多就会耗尽线程池，界面随之卡死。
    /// </para>
    /// <para>
    /// <b>关键：超时后绝不能把挂起的读任务遗弃在后台。</b>
    /// 早期实现用 <c>readTask.Wait(timeout)</c>，超时后直接返回，
    /// 却把挂起的 <c>ReadAsync</c> 留在了流上。此后：
    /// ① 数据到达时被那个「僵尸任务」读走并丢弃，下一轮永远读不到数据；
    /// ② 流上出现并发读，后续读取行为不可预期。
    /// 结果就是被动模式的触发内容永远匹配不上 —— 表现为「无法触发」。
    /// </para>
    /// <para>
    /// 现在改用<b>同步 Read</b>：.NET 的 <c>NetworkStream.Read</c> 会遵守
    /// <c>Socket.ReceiveTimeout</c>，超时抛 <see cref="IOException"/>，
    /// 既不会遗留任务，也不会破坏流的可复用性。
    /// 实际超时时长由 socket 的 <c>ReceiveTimeout</c> 决定（取自界面的「超时时间」），
    /// <paramref name="timeoutMs"/> 仅作为兜底说明保留。
    /// </para>
    /// </summary>
    private static int ReadWithTimeout(Stream s, byte[] buffer, int timeoutMs = 200)
    {
        try
        {
            return s.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            // 接收超时（Socket.ReceiveTimeout 到期）或网络错误：本次没有数据
            return 0;
        }
        catch (SocketException)
        {
            return 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 断开<b>所有</b>客户端连接。
    /// <para>
    /// 仅用于「整体失效」的场景（如连接本身已不可用）。
    /// 单个客户端的发送失败不应调用本方法，否则会误伤其它正常客户端。
    /// </para>
    /// </summary>
    private void CloseAllClients()
    {
        try
        {
            SetIsConnected(false);
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    try { client.Dispose(); } catch { /*单个客户端释放失败不影响其它*/ }
                }
                _clients.Clear();
            }
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
        }
    }

    /// <summary>
    /// 只断开指定的那一个客户端，其余客户端不受影响。
    /// </summary>
    private void CloseClient(TcpClient? client)
    {
        if (client is null) return;
        try
        {
            lock (_clients)
            {
                _clients.Remove(client);
            }
            client.Dispose();
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

    /// <summary>
    /// 取一个当前已连接的客户端。
    /// <para>不占用信号量，调用方拿到后自行负责释放或断开。</para>
    /// </summary>
    private TcpClient? GetConnectedClient()
    {
        lock (_clients)
        {
            return _clients.FirstOrDefault(CheckConnected);
        }
    }

    Stream GetStream()
    {
        // 注意：信号量只用于「挑选客户端」这一个瞬间的保护，
        // 不能在持有期间做阻塞操作（原来的实现会在持有 _sem 时调用 client.GetStream()，
        // 若该调用阻塞，UI 线程也会被一起挂住）。
        bool acquired = false;
        try
        {
            // 加超时，避免信号量异常时无限期挂起调用线程
            acquired = _sem.Wait(TimeSpan.FromSeconds(2));
            if (!acquired) throw new TimeoutException("获取客户端连接超时");

            var client = GetConnectedClient();
            if (client is null) throw new InvalidOperationException("没有已连接的客户端");
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
            // 只有真正获取到信号量才释放，否则会造成计数错乱
            if (acquired) _sem.Release();
        }
    }
    
    public override string Trigger()
    {
        TcpClient? client = null;
        try
        {
            if (!IsEnabled) return"";
            if (!ValidateSendContent()) return "";

            client = GetConnectedClient();
            if (client is null)
            {
                UpdateException("没有已连接的客户端");
                return "";
            }
            var s = client.GetStream();

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
            // 等待回包。用同步读（遵守 socket 的 ReceiveTimeout），
            // 按总时长控制等待时间：不能靠「循环 100 次」，
            // 因为每次读都会阻塞满一个超时周期，累加后可能达到几十秒。
            var deadline = Environment.TickCount + Math.Max(500, ReadTimeOut);
            while (Environment.TickCount < deadline)
            {
                var n = ReadWithTimeout(s, buffer);
                if (n > 0)
                {
                    var str = Encoding.UTF8.GetString(buffer, 0, n);
                    UpdateReceiveContent(str);
                    SetIsConnected(true);
                    return str;
                }
            }

            return "";
        }
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭连接
            UpdateException(ex.Message);
            return "";
        }
        catch (Exception ex)
        {
            // 只断开本次涉及的客户端
            CloseClient(client);
            UpdateException(ex.Message);
            return "";
        }
    }

    public override bool Send()
    {
        TcpClient? client = null;
        try
        {
            if (!IsEnabled) return false;
            // 发送内容不合法属于用户输入问题：提示即可，不该断开连接
            if (!ValidateSendContent()) return false;

            client = GetConnectedClient();
            if (client is null)
            {
                UpdateException("没有已连接的客户端");
                return false;
            }
            return SendToStream(client.GetStream());
        }
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭连接
            UpdateException(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // 连接错误：只断开本次涉及的客户端，不影响其它客户端
            CloseClient(client);
            UpdateException(ex.Message);
            return false;
        }
    }

    public bool Send(TcpClient client)
    {
        try
        {
            if (!IsEnabled) return false;
            if (!ValidateSendContent()) return false;
            return SendToStream(client.GetStream());
        }
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭连接
            UpdateException(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // 只断开这一个客户端
            CloseClient(client);
            UpdateException(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 实际往流里写数据。内容组装逻辑集中在这里，避免各处重复。
    /// </summary>
    private bool SendToStream(Stream s)
    {
        var sendDataList = new List<byte>();
        switch (SendContentType)
        {
            default:
            case EnumContentType.Text:
                if (AppendContent != EnumAppendContent.ParseAndSend)
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(SendContent)); //获取发送内容
                sendDataList.AddRange(Encoding.UTF8.GetBytes(GetAppendContent())); //附加内容
                sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                break;
            case EnumContentType.Hex:
                sendDataList.AddRange(HexStringToByteArray(SendContent)); //获取发送内容
                sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                break;
        }
        var sendData = sendDataList.ToArray();
        s.Write(sendData, 0, sendData.Length); //发送内容
        SetIsConnected(true);
        return true;
    }
    
    public override bool SendString(string str)
    {
        TcpClient? client = null;
        try
        {
            if (!IsEnabled) return false;
            client = GetConnectedClient();
            if (client is null)
            {
                UpdateException("没有已连接的客户端");
                return false;
            }
            var sp = client.GetStream();
            var sendData = Encoding.UTF8.GetBytes(str);
            sp.Write(sendData, 0, sendData.Length); //发送内容
            SetIsConnected(true);
            return true;
        }
        catch (Exception ex)
        {
            // 只断开本次涉及的客户端，不影响其它客户端
            CloseClient(client);
            UpdateException(ex.Message);
            return false;
        }
    }

    public override bool SendData(byte[] data)
    {
        TcpClient? client = null;
        try
        {
            if (!IsEnabled) return false;
            client = GetConnectedClient();
            if (client is null)
            {
                UpdateException("没有已连接的客户端");
                return false;
            }
            var sp = client.GetStream();
            sp.Write(data, 0, data.Length); //发送内容
            SetIsConnected(true);
            return true;
        }
        catch (Exception ex)
        {
            // 只断开本次涉及的客户端，不影响其它客户端
            CloseClient(client);
            UpdateException(ex.Message);
            return false;
        }
    }

    public override bool Cancel()
    {
        TcpClient? client = null;
        try
        {
            if (!IsEnabled) return false;
            client = GetConnectedClient();
            if (client is null)
            {
                UpdateException("没有已连接的客户端");
                return false;
            }
            var s = client.GetStream();

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
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭连接
            UpdateException(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // 只断开本次涉及的客户端
            CloseClient(client);
            UpdateException(ex.Message);
            return false;
        }
    }
}