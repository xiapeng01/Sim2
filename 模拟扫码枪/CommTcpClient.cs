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
                var status = _client != null && CheckConnected(_client);
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
            if (!IsEnabled)
            {
                _client?.Dispose();
                _client = null;
                continue;
            }
            else
            {
                if (_client is null)
                {
                    _client = new TcpClient();
                    _client.ReceiveTimeout = ReadTimeOut;
                    _client.SendTimeout = WriteTimeOut;
                    await _client.ConnectAsync(TcpIP, TcpPort);
                    await Task.Delay(1000);
                }else
                {
                    if(!CheckConnected(_client))
                    {
                        _client.Dispose();
                        _client = null;
                    };
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
            _client?.Dispose();
            _client = null;
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
        }
    }

    Stream GetStream()
    { 
        if (_client is null)
        {
            _client = new TcpClient();
            _client.ReceiveTimeout = ReadTimeOut;
            _client.SendTimeout = WriteTimeOut;
            _client.Connect(TcpIP,TcpPort);
        }
        return _client.GetStream();
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