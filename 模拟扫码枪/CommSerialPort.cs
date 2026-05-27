using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Windows;

namespace 模拟扫码枪;

public class CommSerialPort:AComm
{
    private SerialPort? _sp = null; 
    
    public CommSerialPort()
    { 
        _ = Task.Run(DoWork);//被动接收和仅接受模式 
    }

    private async Task DoWork()
    {
        while (!IsDisposed)
        {
            await Task.Delay(10);//间隔10毫秒
            if (!IsEnabled)
            {
                await Task.Delay(500);
                _sp?.Dispose();
                _sp = null;
                SetIsConnected(false);
                continue;
            }
            else
            {
                if (_sp is null)
                {
                    _sp = new SerialPort(SerialPortName, SerialPortBaudRate, SerialPortParity, SerialPortDataBits,
                        SerialPortStopBits);
                    _sp.ReadTimeout = ReadTimeOut;
                    _sp.WriteTimeout = WriteTimeOut;
                    _sp.Open();
                    SetIsConnected(true);
                    await Task.Delay(1000);//等待1秒
                }
            }
            
            try
            { 
                switch (WorkModel)
                {
                    case EnumWorkModel.Passive:
                        var sp1 = GetSerialPort();
                        if (sp1.BytesToRead > 0)
                        { 
                            var str = sp1.ReadExisting();//读取所有内容
                            UpdateMessage($"收到内容[{str}]");
                            if (IsVerifyTriggerContent)//通过校验才发送
                            {
                                switch (ReceiveContentType)
                                {
                                    default:
                                    case EnumContentType.Text://字符串模式不比较空白字符
                                        var clearTriggerString = TriggerContent.Trim();
                                        if (clearTriggerString.Equals(str.Trim()))
                                        {
                                            Send();
                                        }
                                        break;
                                    case EnumContentType.Hex:
                                        var triggerData = HexStringToByteArray(TriggerContent);
                                        if (CompareByteArray(Encoding.UTF8.GetBytes(str),triggerData))
                                        {
                                            Send();
                                        }
                                        break;
                                }
                            }
                            else//不校验,直接发送
                            {
                                Send();//执行发送内容
                            }
                            
                        }
                        SetIsConnected(true);
                        Debug.WriteLine(DateTime.Now);
                        break;
                    case EnumWorkModel.OnlyReceive:
                        var sp2 = GetSerialPort();
                        if (sp2.BytesToRead > 0)
                        {
                            var str = sp2.ReadExisting();//读取所有内容
                            UpdateMessage($"收到内容[{str}]");
                            UpdateReceiveContent(str);
                        }
                        SetIsConnected(true);
                        Debug.WriteLine(DateTime.Now);
                        break; 
                }
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
            _sp?.Dispose();
            _sp = null;
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
        }
    }

    SerialPort GetSerialPort()
    { 
        if (_sp is null)
        {
            _sp = new SerialPort(SerialPortName, SerialPortBaudRate, SerialPortParity,SerialPortDataBits, SerialPortStopBits);
            _sp.ReadTimeout = ReadTimeOut;
            _sp.WriteTimeout = WriteTimeOut;
            _sp.Open();
        }
        return _sp;
    } 
    
    public override string Trigger()
    {
        try
        {
            if (!IsEnabled) return"";
            var sp = GetSerialPort();
            if (sp.BytesToRead > 0) sp.ReadExisting();//清空接收缓冲区,避免干扰被动接收模式的触发内容校验

            List<byte> sendDataList = new List<byte>();
            byte[] sendData;
            switch (SendContentType)
            {
                default:
                case EnumContentType.Text:
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(SendContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    sp.Write(sendData, 0, sendData.Length); //发送内容
                    break;
                case EnumContentType.Hex:
                    sendDataList.AddRange(HexStringToByteArray(SendContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    sp.Write(sendData, 0, sendData.Length); //发送内容
                    break;
            }

            for (var i = 0; i < 200; i++)
            {
                Thread.Sleep(5);
                if (sp.BytesToRead > 0) break; //跳出
            }

            var str = sp.ReadExisting();
            UpdateReceiveContent(str);
            SetIsConnected(true);
            return str;
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
            var sp = GetSerialPort();

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
                    sp.Write(sendData, 0, sendData.Length); //发送内容
                    break;
                case EnumContentType.Hex:
                    sendDataList.AddRange(HexStringToByteArray(SendContent)); //获取发送内容
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    sp.Write(sendData, 0, sendData.Length); //发送内容
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
            var sp = GetSerialPort(); 
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
            var sp = GetSerialPort();

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
            var sp = GetSerialPort();

            List<byte> sendDataList = new List<byte>();
            byte[] sendData;
            switch (SendContentType)
            {
                default:
                case EnumContentType.Text:
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(CancelContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    sp.Write(sendData, 0, sendData.Length); //发送内容
                    break;
                case EnumContentType.Hex:
                    sendDataList.AddRange(HexStringToByteArray(CancelContent)); //获取发送内容 
                    sendDataList.AddRange(Encoding.UTF8.GetBytes(GetEOF()));
                    sendData = sendDataList.ToArray();
                    sp.Write(sendData, 0, sendData.Length); //发送内容
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