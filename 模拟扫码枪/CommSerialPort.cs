using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Windows;

namespace 模拟扫码枪;

public class CommSerialPort:AComm
{
    private SerialPort? _sp = null;
    // 保护 _sp 的读写：工作循环、发送线程、释放流程会并发访问同一句柄
    private readonly object _spLock = new();

    public CommSerialPort()
    {
        _ = Task.Run(DoWork);//被动接收和仅接受模式
    }

    private async Task DoWork()
    {
        while (!IsDisposed)
        {
            await Task.Delay(10);//间隔10毫秒
            if (IsDisposed) break;
            if (!IsEnabled)
            {
                await Task.Delay(500);
                Close();
                continue;
            }
            else
            {
                if (_sp is null)
                {
                    // 打开串口可能失败（端口被占用/不存在/参数非法）。
                    // 这里必须自己捕获并重试，否则异常会冲出 while 循环，
                    // 导致整个工作线程永久退出，模拟器再也无法工作且无任何提示。
                    if (!TryOpenSerialPort())
                    {
                        await Task.Delay(2000);//失败后退避 2 秒再重试
                        continue;
                    }
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
                            // 用 Read 拿原始字节，而不是 ReadExisting()：
                            // HEX 模式的触发比对需要原始字节，
                            // ReadExisting() 返回的是已按编码解码的字符串，字节信息已丢失
                            var raw = new byte[sp1.BytesToRead];
                            var readCount = sp1.Read(raw, 0, raw.Length);
                            var str = Encoding.UTF8.GetString(raw, 0, readCount);
                            UpdateMessage($"收到内容[{str}]");
                            // 触发匹配统一走基类：HEX 模式用原始字节比较，
                            // 不能用「解码成字符串再编码回来」（对非 UTF-8 合法字节有损）
                            if (IsTriggerMatched(raw, str))
                            {
                                Send(); //执行发送内容
                            }
                        }
                        SetIsConnected(true);
                        TraceDebug(DateTime.Now.ToString());
                        break;
                    case EnumWorkModel.OnlyReceive:
                        var sp2 = GetSerialPort();
                        if (sp2.BytesToRead > 0)
                        {
                            var raw2 = new byte[sp2.BytesToRead];
                            var readCount2 = sp2.Read(raw2, 0, raw2.Length);
                            var str = Encoding.UTF8.GetString(raw2, 0, readCount2);//读取所有内容
                            UpdateMessage($"收到内容[{str}]");
                            UpdateReceiveContent(str);
                        }
                        SetIsConnected(true);
                        TraceDebug(DateTime.Now.ToString());
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
            lock (_spLock)
            {
                _sp?.Dispose();
                _sp = null;
            }
        }
        catch (Exception ex)
        {
            UpdateException(ex.Message);
        }
    }

    /// <summary>
    /// 尝试打开串口。成功返回 true；失败时上报异常并返回 false，由调用方退避后重试。
    /// </summary>
    private bool TryOpenSerialPort()
    {
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(SerialPortName, SerialPortBaudRate, SerialPortParity, SerialPortDataBits,
                SerialPortStopBits);
            sp.ReadTimeout = ReadTimeOut;
            sp.WriteTimeout = WriteTimeOut;
            sp.Open();
            lock (_spLock)
            {
                if (IsDisposed)
                {
                    // 释放流程与打开流程并发，直接丢弃刚打开的句柄
                    sp.Dispose();
                    return false;
                }
                _sp = sp;
            }
            return true;
        }
        catch (Exception ex)
        {
            try { sp?.Dispose(); } catch { /*忽略*/ }
            lock (_spLock) { _sp = null; }
            UpdateException($"打开串口 [{SerialPortName}] 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>释放串口句柄</summary>
    protected override void ReleaseResources()
    {
        try
        {
            lock (_spLock)
            {
                _sp?.Dispose();
                _sp = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("释放串口失败:" + ex.Message);
        }
    }

    SerialPort GetSerialPort()
    {
        lock (_spLock)
        {
            if (_sp is null)
            {
                // 打开失败时抛出，由调用方（已有 try/catch）处理，
                // 不能让异常逃逸出工作循环导致整个模拟器永久失效。
                var sp = new SerialPort(SerialPortName, SerialPortBaudRate, SerialPortParity, SerialPortDataBits, SerialPortStopBits);
                sp.ReadTimeout = ReadTimeOut;
                sp.WriteTimeout = WriteTimeOut;
                sp.Open();
                _sp = sp;
            }
            return _sp;
        }
    }
    
    public override string Trigger()
    {
        try
        {
            if (!IsEnabled) return"";
            // 发送内容不合法属于用户输入问题：提示即可，不该断开串口
            if (!ValidateSendContent()) return "";
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

            // 等待回包。原先固定等待最多 1 秒（200 × 5ms），
            // 这里改成按配置的超时时间等待，且采用短间隔轮询，
            // 一旦有数据立刻返回，避免不必要的长时间阻塞。
            var deadline = Environment.TickCount + Math.Max(100, ReadTimeOut);
            while (Environment.TickCount < deadline)
            {
                if (sp.BytesToRead > 0) break; //有数据了，跳出等待
                Thread.Sleep(5);
            }

            var str = sp.ReadExisting();
            UpdateReceiveContent(str);
            SetIsConnected(true);
            return str;
        }
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭串口
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

    public override bool Send()
    {
        try
        {
            if (!IsEnabled) return false;
            // 发送内容不合法属于用户输入问题：提示即可，不该断开串口
            if (!ValidateSendContent()) return false;
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
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭串口
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
        catch (ContentArgumentException ex)
        {
            // 参数错误：不关闭串口
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