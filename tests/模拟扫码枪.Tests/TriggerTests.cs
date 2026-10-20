using System.Net;
using System.Net.Sockets;
using System.Text;

namespace 模拟扫码枪.Tests;

/// <summary>
/// 触发链路的回归测试。
///
/// <para>背景：用户反馈「无法触发」。走查后定位到两个真实缺陷：</para>
/// <list type="number">
/// <item>
/// <b>读超时后遗留「僵尸读任务」</b>：<c>ReadWithTimeout</c> 原先用
/// <c>readTask.Wait(timeout)</c>，超时后把挂起的 <c>ReadAsync</c> 遗弃在流上。
/// 数据到达时被那个僵尸任务读走并丢弃，下一轮永远读不到数据，
/// 于是被动模式的触发内容永远匹配不上。TCP 服务端用的是这个实现，所以必然失败。
/// </item>
/// <item>
/// <b>HEX 触发校验用「字符串中转」比较</b>：原实现把收到的字节解码成字符串、
/// 再用 <c>Encoding.UTF8.GetBytes</c> 编码回来与触发内容比较。
/// 这个往返对非 UTF-8 合法字节是有损的（<c>80 FE 41</c> → <c>EF BF BD EF BF BD 41</c>），
/// 导致 HEX 模式永远匹配不上。现在改为直接用原始字节比较。
/// </item>
/// </list>
///
/// <para>这些用例覆盖「主动触发」与「被动触发」两条主路径，是「无法触发」问题的长期防线。</para>
/// </summary>
[Trait("Category", "Slow")]
public class TriggerTests
{
    /// <summary>取一个当前空闲的端口号</summary>
    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---------- 被动触发（扫码枪模拟器的主要用法）----------

    [Fact]
    public async Task 被动模式_文本触发内容匹配后应自动回发()
    {
        int port = GetFreePort();
        using var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            SendContent = "BARCODE-TEXT",
            TriggerContent = "TRIGGER",
            IsVerifyTriggerContent = true,
            ReceiveContentType = EnumContentType.Text,
            WorkModel = EnumWorkModel.Passive,
            IsEnabled = true
        };

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var received = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
            });

            // 等客户端连上
            await Task.Delay(1500);

            // 对端发来触发内容
            var trig = Encoding.UTF8.GetBytes("TRIGGER");
            await peer.GetStream().WriteAsync(trig, 0, trig.Length);
            await peer.GetStream().FlushAsync();

            var finished = await Task.WhenAny(received.Task, Task.Delay(5000));
            Assert.True(finished == received.Task, "被动模式下应在超时前收到回包");
            Assert.Contains("BARCODE-TEXT", await received.Task);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 被动模式_HEX触发内容匹配后应自动回发()
    {
        int port = GetFreePort();
        using var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            SendContent = "BARCODE-HEX",
            // 触发内容用十六进制表示 16 54 0D（扫码枪典型触发帧）
            TriggerContent = "16540D",
            IsVerifyTriggerContent = true,
            ReceiveContentType = EnumContentType.Hex,
            WorkModel = EnumWorkModel.Passive,
            IsEnabled = true
        };

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var received = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
            });

            await Task.Delay(1500);

            // 对端发来原始字节 16 54 0D
            var trig = new byte[] { 0x16, 0x54, 0x0D };
            await peer.GetStream().WriteAsync(trig, 0, trig.Length);
            await peer.GetStream().FlushAsync();

            var finished = await Task.WhenAny(received.Task, Task.Delay(5000));
            Assert.True(finished == received.Task, "HEX 模式下应在超时前收到回包");
            Assert.Contains("BARCODE-HEX", await received.Task);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 被动模式_HEX触发内容为非UTF8字节时也应匹配()
    {
        int port = GetFreePort();
        using var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            SendContent = "BARCODE-RAW",
            // 80 FE 41 不是合法的 UTF-8 序列：
            // 若用「解码成字符串再编码回来」比较，会变成 EF BF BD EF BF BD 41，永远匹配不上
            TriggerContent = "80FE41",
            IsVerifyTriggerContent = true,
            ReceiveContentType = EnumContentType.Hex,
            WorkModel = EnumWorkModel.Passive,
            IsEnabled = true
        };

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var received = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
            });

            await Task.Delay(1500);

            var trig = new byte[] { 0x80, 0xFE, 0x41 };
            await peer.GetStream().WriteAsync(trig, 0, trig.Length);
            await peer.GetStream().FlushAsync();

            var finished = await Task.WhenAny(received.Task, Task.Delay(5000));
            Assert.True(finished == received.Task, "非 UTF-8 字节的 HEX 触发也应当匹配");
            Assert.Contains("BARCODE-RAW", await received.Task);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 被动模式_触发内容不匹配时不应回发()
    {
        int port = GetFreePort();
        using var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            SendContent = "SHOULD-NOT-SEND",
            TriggerContent = "TRIGGER",
            IsVerifyTriggerContent = true,
            ReceiveContentType = EnumContentType.Text,
            WorkModel = EnumWorkModel.Passive,
            IsEnabled = true
        };

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var received = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
            });

            await Task.Delay(1500);

            // 发一个不匹配的触发内容
            var trig = Encoding.UTF8.GetBytes("SOMETHING-ELSE");
            await peer.GetStream().WriteAsync(trig, 0, trig.Length);
            await peer.GetStream().FlushAsync();

            var finished = await Task.WhenAny(received.Task, Task.Delay(2500));
            Assert.False(finished == received.Task, "触发内容不匹配时不应回发");
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---------- 主动触发 ----------

    [Fact]
    public async Task 主动触发_客户端应把内容发给对端()
    {
        int port = GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            var received = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var peer = await listener.AcceptTcpClientAsync();
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
            });

            using var comm = new CommTcpClient
            {
                TcpIP = "127.0.0.1",
                TcpPort = port,
                SendContent = "HELLO-SCAN",
                WorkModel = EnumWorkModel.Trigger,
                IsEnabled = true
            };

            await Task.Delay(1500);
            comm.Trigger();

            var finished = await Task.WhenAny(received.Task, Task.Delay(5000));
            Assert.True(finished == received.Task, "主动触发应把内容发给对端");
            Assert.Contains("HELLO-SCAN", await received.Task);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 主动触发_服务端应把内容发给客户端()
    {
        var comm = new CommTcpServer
        {
            TcpPort = 0,   // 由系统分配
            SendContent = "SRV-SCAN",
            WorkModel = EnumWorkModel.Trigger,
            IsEnabled = true
        };

        try
        {
            await Task.Delay(1200);

            // 反射取实际监听端口
            int port = 0;
            var f = typeof(CommTcpServer).GetField("_server",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f?.GetValue(comm) is TcpListener lst)
                port = ((IPEndPoint)lst.LocalEndpoint).Port;
            Assert.True(port > 0, "服务端应已监听端口");

            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, port);

            var received = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
            });

            await Task.Delay(500);
            comm.Trigger();

            var finished = await Task.WhenAny(received.Task, Task.Delay(5000));
            Assert.True(finished == received.Task, "服务端主动触发应把内容发给客户端");
            Assert.Contains("SRV-SCAN", await received.Task);
        }
        finally
        {
            comm.Dispose();
        }
    }

    // ---------- 命令可用性 ----------

    [Fact]
    public void 发送命令_未启用时不应可执行()
    {
        using var comm = new CommTcpClient { WorkModel = EnumWorkModel.Trigger, IsEnabled = false };
        Assert.False(comm.SendCommand.CanExecute(null));
    }

    [Fact]
    public void 发送命令_已启用且为主动触发时应可执行()
    {
        using var comm = new CommTcpClient { WorkModel = EnumWorkModel.Trigger, IsEnabled = true };
        Assert.True(comm.SendCommand.CanExecute(null));
    }

    [Fact]
    public void 发送命令_仅接收模式下不应可执行()
    {
        using var comm = new CommTcpClient { WorkModel = EnumWorkModel.OnlyReceive, IsEnabled = true };
        Assert.False(comm.SendCommand.CanExecute(null));
    }
}
