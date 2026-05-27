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
                if (_clients.All(CheckConnected)) continue;

                try
                {
                    await _sem.WaitAsync();
                    _clients.RemoveAll(a => !CheckConnected(a));
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
                var status = _clients.Any(CheckConnected);
                Post(() => SetIsConnected(status));
                var count= _clients.Count;
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
                if(_server is null) continue;
                var client=await _server.AcceptTcpClientAsync();
                _=Task.Run(() => DoClientWork(client));
                _clients.Add(client);
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
            if (!IsEnabled)
            {
                try
                {
                    foreach (var client in _clients)
                    {
                        client.Dispose();
                    }
                    _clients.Clear();
                    _server?.Stop();
                    _server = null;
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
                continue;
            }

            if (_server == null)
            {
                _server = new TcpListener(IPAddress.Any, TcpPort);
                _server.Start();
            }

            return;

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

    async Task DoClientWork(TcpClient? client)
    {
        while(!IsDisposed &&(client !=null) && ! CheckConnected(client))
        {
            try
            {
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
            foreach (var client in _clients)
            {
                client.Dispose();
            }
            _clients.Clear();
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
        }
    }

    Stream GetStream()
    {
        try
        {
            _sem.Wait();
            var client = _clients.FirstOrDefault(CheckConnected);
            if (client is null) throw new Exception();
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