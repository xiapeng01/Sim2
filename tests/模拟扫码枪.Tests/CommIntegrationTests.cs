using System.Net;
using System.Net.Sockets;
using System.Text;

namespace 模拟扫码枪.Tests;

/// <summary>
/// 通信层的集成测试。使用真实回环端口，验证 P0 修复后的重试与释放行为。
/// 这些用例较慢（需要等待重试退避时间），但对"静默失效"这类问题是最有效的防线。
/// </summary>
[Trait("Category", "Slow")]
public class CommIntegrationTests
{
    /// <summary>取一个当前空闲的端口号（用 Any 探测，与 TcpServer 的绑定方式保持一致）</summary>
    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---------- TCP 客户端 ----------

    [Fact]
    public async Task TcpClient_对端未启动时应持续重试而不失效()
    {
        int port = GetFreePort();
        using var comm = new CommTcpClient { TcpIP = "127.0.0.1", TcpPort = port, IsEnabled = true };

        int errors = 0;
        comm.UpdateExceptionEvent += _ => Interlocked.Increment(ref errors);

        // 退避为 2 秒，等待足够长的时间以确认循环没有被异常终止
        await Task.Delay(5200);

        int snapshot = Volatile.Read(ref errors);
        Assert.True(snapshot > 0, "连接失败应产生错误上报（说明循环仍在运行并重试）");
    }

    [Fact]
    public async Task TcpClient_端点就绪后应自动连接()
    {
        int port = GetFreePort();
        using var comm = new CommTcpClient { TcpIP = "127.0.0.1", TcpPort = port, IsEnabled = true };

        // 先让客户端经历失败重试
        await Task.Delay(1200);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            // 等待退避重试命中
            for (int i = 0; i < 40 && !comm.IsConnected; i++)
            {
                await Task.Delay(200);
            }
            Assert.True(comm.IsConnected, "对端就绪后客户端应自动恢复连接（P0 #2 的回归防线）");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpClient_禁用后连接应被释放()
    {
        int port = GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var comm = new CommTcpClient { TcpIP = "127.0.0.1", TcpPort = port, IsEnabled = true };
        for (int i = 0; i < 40 && !comm.IsConnected; i++) await Task.Delay(200);
        Assert.True(comm.IsConnected);

        comm.IsEnabled = false;
        await Task.Delay(1500);
        Assert.False(comm.IsConnected);

        comm.Dispose();
        listener.Stop();
    }

    [Fact]
    public async Task TcpClient_发送内容应被对端收到()
    {
        int port = GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync();

        using var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            IsEnabled = true,
            SendContent = "PING",
            EndOfFrame = EnumEndOfFrame.CRLR
        };

        for (int i = 0; i < 40 && !comm.IsConnected; i++) await Task.Delay(200);

        Assert.True(comm.SendString("PING"), "发送应成功");

        using var server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
        var buffer = new byte[64];
        int n = await server.GetStream().ReadAsync(buffer);
        Assert.Equal("PING", Encoding.UTF8.GetString(buffer, 0, n));

        listener.Stop();
    }

    // ---------- TCP 服务端 ----------

    [Fact]
    public async Task TcpServer_Dispose后监听端口应释放()
    {
        int port = GetFreePort();
        var comm = new CommTcpServer { TcpPort = port, IsEnabled = true };

        // 等待监听建立
        bool listening = false;
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(200);
            if (IsPortBusy(port)) { listening = true; break; }
        }
        Assert.True(listening, "服务端应成功监听端口");

        comm.Dispose();
        await Task.Delay(800);

        // Dispose 后端口必须可被重新绑定（P0 #4 的回归防线）
        var probe = new TcpListener(IPAddress.Any, port);
        var ex = Record.Exception(() =>
        {
            probe.Start();
            probe.Stop();
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task TcpServer_客户端连接后应更新连接状态与数量()
    {
        int port = GetFreePort();
        using var comm = new CommTcpServer { TcpPort = port, IsEnabled = true };

        for (int i = 0; i < 40 && !IsPortBusy(port); i++) await Task.Delay(200);

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        // CheckConnectedTask 每 500ms 轮询一次
        for (int i = 0; i < 30 && comm.ClientCount == 0; i++) await Task.Delay(200);
        Assert.True(comm.ClientCount >= 1, "客户端连接数应被统计");

        client.Dispose();
    }

    [Fact]
    public void TcpServer_端口被占用时应上报错误且不死循环崩溃()
    {
        int port = GetFreePort();
        // 必须用 Any 抢占端口，否则与 TcpServer 的 0.0.0.0 绑定不冲突，测不出冲突场景
        var blocker = new TcpListener(IPAddress.Any, port);
        blocker.Start();

        using var comm = new CommTcpServer { TcpPort = port, IsEnabled = true };
        var errors = new List<string>();
        comm.UpdateExceptionEvent += msg =>
        {
            lock (errors) errors.Add(msg);
        };

        Thread.Sleep(3000);

        // 关键：即使端口冲突，异常也必须被捕获上报，而不是冲出工作循环
        lock (errors)
        {
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("监听"));
        }

        blocker.Stop();
    }

    // ---------- 串口 ----------

    [Fact]
    public void 串口_打开失败应上报错误而不抛出()
    {
        // 使用一个几乎不可能存在的端口号
        using var comm = new CommSerialPort
        {
            SerialPortName = "COM99",
            SerialPortBaudRate = 115200,
            IsEnabled = true
        };

        var errors = new List<string>();
        comm.UpdateExceptionEvent += msg =>
        {
            lock (errors) errors.Add(msg);
        };

        Thread.Sleep(2500);

        // 不断言一定失败（极端情况下 COM99 可能存在），
        // 只断言进程没有崩溃，且若有错误则已被上报
        lock (errors) { Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e))); }
    }

    [Fact]
    public void 串口_未启用时发送应返回false()
    {
        using var comm = new CommSerialPort { IsEnabled = false };
        Assert.False(comm.Send());
        Assert.False(comm.SendString("X"));
        Assert.False(comm.SendData(new byte[] { 1 }));
        Assert.False(comm.Cancel());
    }

    [Fact]
    public void TcpClient_未启用时发送应返回false()
    {
        using var comm = new CommTcpClient { IsEnabled = false };
        Assert.False(comm.Send());
        Assert.False(comm.SendString("X"));
        Assert.False(comm.SendData(new byte[] { 1 }));
        Assert.False(comm.Cancel());
        Assert.Equal("", comm.Trigger());
    }

    [Fact]
    public void TcpServer_未启用时发送应返回false()
    {
        using var comm = new CommTcpServer { IsEnabled = false };
        Assert.False(comm.Send());
        Assert.False(comm.Cancel());
    }

    // ---------- 生命周期 ----------

    [Fact]
    public void 三种通信类型_Dispose均应幂等不抛异常()
    {
        var comms = new AComm[]
        {
            new CommSerialPort(),
            new CommTcpClient(),
            new CommTcpServer()
        };

        foreach (var c in comms)
        {
            c.Dispose();
            var ex = Record.Exception(() => c.Dispose());
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Dispose后启用开关应被关闭()
    {
        var c = new CommSerialPort { IsEnabled = true };
        c.Dispose();
        Assert.False(c.IsEnabled);
    }

    /// <summary>
    /// 判断端口是否已被监听。
    /// 必须用 <see cref="IPAddress.Any"/> 探测——TcpServer 绑定的正是 0.0.0.0，
    /// 若用 127.0.0.1 探测不会与之冲突，会误判为"未监听"。
    /// </summary>
    private static bool IsPortBusy(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            l.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
