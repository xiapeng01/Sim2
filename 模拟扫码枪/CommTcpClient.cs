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
                        // 用带超时的读，而不是阻塞的 s.Read()：
                        // 阻塞读会一直占住工作循环，导致后续的连接检查/取消命令无法响应
                        int n = ReadWithTimeout(s, buffer);
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
                                Send();
                            }
                        }


                        SetIsConnected(true);
                        TraceDebug(DateTime.Now.ToString());
                        break;
                    case EnumWorkModel.OnlyReceive:
                        var s2 = GetStream();
                        var buffer2 = new byte[4096];
                        var n2 = ReadWithTimeout(s2, buffer2);
                        if (n2 > 0)
                        {
                            var str = Encoding.UTF8.GetString(buffer2, 0, n2); //读取所有内容
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
            else
            {
                // 复用已有连接时也要保证超时设置是最新的：
                // 同步 Read 依赖 Socket.ReceiveTimeout 才能及时返回，
                // 否则会一直阻塞到对端发数据，工作循环被拖死。
                _client.ReceiveTimeout = ReadTimeOut;
                _client.SendTimeout = WriteTimeOut;
            }
            return _client.GetStream();
        }
    }
    
    public override string Trigger()
    {
        try
        {
            if (!IsEnabled) return"";
            // 发送内容不合法属于用户输入问题：提示即可，不该断开连接
            if (!ValidateSendContent()) return "";
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
            Close();
            UpdateException(ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 带超时的读取。超时返回 0（本次没有数据），不抛异常、不阻塞调用线程。
    /// <para>
    /// <b>超时时绝不能把挂起的读任务遗弃在后台</b>：早期实现用 <c>readTask.Wait(timeout)</c>，
    /// 超时后把挂起的 <c>ReadAsync</c> 留在流上，数据到达时会被那个「僵尸任务」
    /// 读走并丢弃，后续永远读不到数据 —— 表现为「无法触发」。
    /// 改用<b>同步 Read</b>：.NET 的 <c>NetworkStream.Read</c> 会遵守
    /// <c>Socket.ReceiveTimeout</c>，超时抛 <see cref="IOException"/>，不遗留任务。
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
            // 接收超时或网络错误
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

    public override bool Send()
    {
        try
        {
            if (!IsEnabled) return false;
            // 发送内容不合法属于用户输入问题：提示即可，不该断开连接
            if (!ValidateSendContent()) return false;
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
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭连接
            UpdateException(ex.Message);
            return false;
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
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭连接
            UpdateException(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Close();
            UpdateException(ex.Message);
            return false;
        }
    }
}