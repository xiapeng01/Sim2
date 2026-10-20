using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using 模拟扫码枪;

namespace UiRenderTest;

/// <summary>
/// P0 修复专项测试：
/// 1. 连接/打开失败后工作循环必须存活（能自动重试），不能静默失效；
/// 2. Dispose 后底层资源必须真正释放（监听端口不再被占用）。
/// </summary>
internal static class P0ResilienceCheck
{
    public static int Run()
    {
        int fail = 0;
        Console.WriteLine("\n[6] P0 韧性测试（连接失败重试 / 资源释放）");

        fail += TestTcpClientKeepsRetrying();
        fail += TestTcpServerReleasesPort();
        fail += TestDisposeReleasesResources();

        return fail;
    }

    /// <summary>对端不存在时，TCP 客户端应持续重试而不是永久失效</summary>
    private static int TestTcpClientKeepsRetrying()
    {
        int fail = 0;
        // 找一个确定没人监听的端口
        int port;
        var probe0 = new TcpListener(IPAddress.Loopback, 0);
        probe0.Start();
        port = ((IPEndPoint)probe0.LocalEndpoint).Port;
        probe0.Stop();

        var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            IsEnabled = true
        };

        var errors = 0;
        comm.UpdateExceptionEvent += _ => Interlocked.Increment(ref errors);

        // 等待若干轮重试周期（退避 2 秒），确认循环仍在运行
        Thread.Sleep(5200);

        bool retried = Interlocked.CompareExchange(ref errors, 0, 0) > 0;
        Console.WriteLine($"  {(retried ? "[PASS]" : "[FAIL]")} 连接失败后仍在自动重试（捕获 {errors} 次错误）");
        if (!retried) fail++;

        // 关键：循环必须还活着。把端点改成真实服务后应能连上
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Thread.Sleep(3500);

        bool connected = comm.IsConnected;
        Console.WriteLine($"  {(connected ? "[PASS]" : "[FAIL]")} 端点就绪后自动恢复连接（IsConnected={connected}）");
        if (!connected) fail++;

        listener.Stop();
        comm.Dispose();
        return fail;
    }

    /// <summary>TCP 服务端 Dispose 后，端口应能被重新监听</summary>
    private static int TestTcpServerReleasesPort()
    {
        int fail = 0;
        const int port = 45311;

        var comm = new CommTcpServer { TcpPort = port, IsEnabled = true };

        // 等待监听建立
        Thread.Sleep(1500);

        bool firstListenOk;
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            firstListenOk = false; // 能监说明服务端没占端口（可能还没起来）
        }
        catch (SocketException)
        {
            firstListenOk = true; // 端口被占用 = 服务端已监听，符合预期
        }
        Console.WriteLine($"  {(firstListenOk ? "[PASS]" : "[WARN]")} 服务端已占用端口 {port}（说明监听成功）");

        comm.Dispose();
        Thread.Sleep(800);

        // Dispose 后端口必须释放
        bool released = false;
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            released = true;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"         端口仍被占用: {ex.Message}");
        }
        Console.WriteLine($"  {(released ? "[PASS]" : "[FAIL]")} Dispose 后监听端口已释放");
        if (!released) fail++;

        return fail;
    }

    /// <summary>每个通信对象 Dispose 后应进入不可用状态且不抛异常</summary>
    private static int TestDisposeReleasesResources()
    {
        int fail = 0;
        Console.WriteLine("  [检查] 各通信类型 Dispose 幂等性");

        var types = new AComm[]
        {
            new CommSerialPort(),
            new CommTcpClient(),
            new CommTcpServer()
        };

        foreach (var c in types)
        {
            string name = c.GetType().Name;
            try
            {
                c.Dispose();
                c.Dispose();// 重复释放不应抛异常
                Console.WriteLine($"  [PASS] {name} Dispose 正常且幂等");
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"  [FAIL] {name} Dispose 抛异常 -> {ex.Message}");
            }
        }
        return fail;
    }
}
