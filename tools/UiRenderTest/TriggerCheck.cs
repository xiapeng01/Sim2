using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using 模拟扫码枪;

namespace UiRenderTest;

/// <summary>
/// 「无法触发」专项排查探针。
///
/// <para>用户反馈：模拟器配好后触发不了。逐条验证触发链路上每个环节是否成立：</para>
/// <list type="number">
/// <item>命令 CanExecute：未启用 / 工作模式不匹配时按钮应当是禁用的（这是预期行为，不是 bug）</item>
/// <item>HEX 触发校验：被动模式下收到内容能否与触发内容匹配</item>
/// <item>文本触发校验</item>
/// <item>TCP 服务端 / 客户端 主动触发是否真的把数据发出去了</item>
/// </list>
///
/// <para>用法：UiRenderTest.exe trigger</para>
/// </summary>
internal static class TriggerCheck
{
    public static int Run()
    {
        int fail = 0;
        Console.WriteLine("\n[T] 触发链路排查");

        fail += CheckCommandCanExecute();
        fail += CheckHexTriggerMatch();
        fail += CheckTextTriggerMatch();
        fail += CheckTcpClientTrigger();
        fail += CheckTcpServerTrigger();
        fail += CheckPassiveTriggerServer();
        fail += CheckPassiveTriggerClient();
        fail += CheckPassiveHexTrigger();
        fail += CheckConcurrentReadHypothesis();
        fail += CheckCancelBreaksStream();
        fail += CheckSyncReadWithSocketTimeout();

        Console.WriteLine(fail == 0 ? "\n[T] 触发链路全部正常" : $"\n[T] {fail} 处异常");
        return fail;
    }

    static void Line(string s) => Console.WriteLine("  " + s);

    /// <summary>命令的 CanExecute 条件是否随 IsEnabled / WorkModel 正确变化</summary>
    private static int CheckCommandCanExecute()
    {
        int fail = 0;
        Line("--- A: SendCommand 的 CanExecute 条件 ---");
        var c = new CommTcpClient();
        try
        {
            c.WorkModel = EnumWorkModel.Trigger;
            c.IsEnabled = false;
            bool canWhenDisabled = c.SendCommand.CanExecute(null);
            Line($"未启用时 CanExecute = {canWhenDisabled}（期望 False）");
            if (canWhenDisabled) { fail++; Line("!! 未启用却可执行"); }

            c.IsEnabled = true;
            bool canWhenEnabled = c.SendCommand.CanExecute(null);
            Line($"已启用+主动触发 CanExecute = {canWhenEnabled}（期望 True）");
            if (!canWhenEnabled) { fail++; Line("!! 已启用却不可执行"); }

            c.WorkModel = EnumWorkModel.OnlyReceive;
            bool canInOnlyReceive = c.SendCommand.CanExecute(null);
            Line($"已启用+仅接收 CanExecute = {canInOnlyReceive}（期望 False）");
            if (canInOnlyReceive) { fail++; Line("!! 仅接收模式却可执行"); }
        }
        finally { c.Dispose(); }
        return fail;
    }

    /// <summary>HEX 模式的触发内容比对：这是历史报告里的 P1-7，需要精确验证</summary>
    private static int CheckHexTriggerMatch()
    {
        int fail = 0;
        Line("--- B: HEX 模式触发校验 ---");

        // 场景：对端真正发来的是字节 16 54 0D（扫码枪的典型触发帧）
        // 模拟器按 UTF8 解码成字符串再比较 —— 这是现实现的路径
        var realBytes = new byte[] { 0x16, 0x54, 0x0D };
        var decoded = Encoding.UTF8.GetString(realBytes);
        var triggerContent = "16540D";                 // 用户在界面上填的触发内容
        var triggerBytes = HexToBytes(triggerContent); // 现实现解析出的字节

        Line($"对端实际发来   = [{string.Join(" ", realBytes.Select(b => b.ToString("X2")))}]");
        Line($"UTF8 解码得到  = \"{decoded.Replace("\r", "\\r").Replace("\u0016", "\\u0016")}\"");
        Line($"触发内容       = \"{triggerContent}\" -> 解析为 [{string.Join(" ", triggerBytes.Select(b => b.ToString("X2")))}]");

        // 现实现：Encoding.UTF8.GetBytes(解码后的字符串) 与 触发内容解析出的字节 比较
        var roundTrip = Encoding.UTF8.GetBytes(decoded);
        bool ok = roundTrip.Length == triggerBytes.Length
                  && !roundTrip.Where((t, i) => t != triggerBytes[i]).Any();
        Line($"现实现比较结果 = {ok}（期望 True）");
        if (!ok)
        {
            fail++;
            Line("!! 合法 UTF8 字节也会失配 —— 结论：解码再编码不是无损的");
        }

        // 再验证非 UTF8 合法字节的情况（>=0x80 的裸字节）
        var rawBytes = new byte[] { 0x80, 0xFE, 0x41 };
        var rawDecoded = Encoding.UTF8.GetString(rawBytes);
        var rawRoundTrip = Encoding.UTF8.GetBytes(rawDecoded);
        bool rawOk = rawRoundTrip.Length == rawBytes.Length
                     && !rawRoundTrip.Where((t, i) => t != rawBytes[i]).Any();
        Line($"非 UTF8 字节   = [{string.Join(" ", rawBytes.Select(b => b.ToString("X2")))}] -> 解码再编码 = [{string.Join(" ", rawRoundTrip.Select(b => b.ToString("X2")))}]");
        Line($"旧写法往返无损 = {rawOk}（期望 False —— 旧实现确实失配，这就是 P1-7）");
        if (rawOk)
        {
            fail++;
            Line("?? 旧写法居然无损，结论需要重新评估");
        }

        // 新实现：直接用原始字节比较，必须匹配
        var triggerBytes2 = HexToBytes("80FE41");
        bool newOk = rawBytes.Length == triggerBytes2.Length
                     && !rawBytes.Where((t, i) => t != triggerBytes2[i]).Any();
        Line($"新写法（原始字节直接比较）= {newOk}（期望 True）");
        if (!newOk)
        {
            fail++;
            Line("!! 新写法未能匹配原始字节");
        }
        return fail;
    }

    /// <summary>文本模式的触发内容比对</summary>
    private static int CheckTextTriggerMatch()
    {
        int fail = 0;
        Line("--- C: 文本模式触发校验 ---");
        var received = "SCAN123";
        var triggerContent = "SCAN123";
        bool ok = triggerContent.Trim().Equals(received.Trim());
        Line($"收到 \"{received}\" 与触发内容 \"{triggerContent}\" 比较 = {ok}（期望 True）");
        if (!ok) { fail++; Line("!! 文本模式也无法匹配"); }
        return fail;
    }

    /// <summary>TCP 客户端主动触发：数据是否真的发出去了</summary>
    private static int CheckTcpClientTrigger()
    {
        int fail = 0;
        Line("--- D: TCP 客户端主动触发 ---");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var received = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var buf = new byte[4096];
                int n = await client.GetStream().ReadAsync(buf, 0, buf.Length);
                received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
        });

        var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            SendContent = "HELLO-SCAN",
            WorkModel = EnumWorkModel.Trigger,
            IsEnabled = true
        };

        try
        {
            // 等待连接建立
            Thread.Sleep(1500);
            var r = comm.Trigger();
            Line($"Trigger() 返回 = \"{r}\"");
            // 注意：IsConnected 由 500ms 轮询的 CheckConnectedTask 更新，
            // 且本用例的对端读完数据后立刻 Dispose 掉连接，
            // 因此这里读到 False 是探针自身行为，不代表连接有问题。
            Line($"IsConnected = {comm.IsConnected}（对端已断开，属探针行为）");

            bool got = received.Task.Wait(3000);
            string data = got ? received.Task.Result : "(超时未收到)";
            Line($"对端收到 = \"{data}\"");
            if (!got || !data.Contains("HELLO-SCAN"))
            {
                fail++;
                Line("!! 主动触发没有把内容发出去");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            comm.Dispose();
            listener.Stop();
        }
        return fail;
    }

    /// <summary>TCP 服务端主动触发：数据是否真的发出去了</summary>
    private static int CheckTcpServerTrigger()
    {
        int fail = 0;
        Line("--- E: TCP 服务端主动触发 ---");
        return CheckTcpServerTriggerAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> CheckTcpServerTriggerAsync()
    {
        int fail = 0;

        var comm = new CommTcpServer
        {
            TcpPort = 0,   // 0 = 由系统分配
            SendContent = "SRV-SCAN",
            WorkModel = EnumWorkModel.Trigger,
            IsEnabled = true
        };

        try
        {
            Thread.Sleep(1200);
            int port = 0;
            // 通过反射拿实际监听端口
            var f = typeof(CommTcpServer).GetField("_server",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f?.GetValue(comm) is TcpListener lst)
            {
                port = ((IPEndPoint)lst.LocalEndpoint).Port;
            }
            Line($"实际监听端口 = {port}");
            if (port == 0) { fail++; Line("!! 未能取得监听端口"); return fail; }

            var received = new TaskCompletionSource<string>();
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var buf = new byte[4096];
                        int n = await client.GetStream().ReadAsync(buf, 0, buf.Length);
                        received.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                    }
                    catch (Exception ex) { received.TrySetResult("EX:" + ex.Message); }
                });

                Thread.Sleep(500);
                var r = comm.Trigger();
                Line($"Trigger() 返回 = \"{r}\"");

                bool got = received.Task.Wait(3000);
                string data = got ? received.Task.Result : "(超时未收到)";
                Line($"客户端收到 = \"{data}\"");
                if (!got || !data.Contains("SRV-SCAN"))
                {
                    fail++;
                    Line("!! 服务端主动触发没有把内容发出去");
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            comm.Dispose();
        }
        return fail;
    }

    static byte[] HexToBytes(string s)
    {
        s = (s ?? "").Replace(" ", "").ToUpper();
        if (s.Length % 2 != 0) s += "0";
        var ret = new byte[s.Length / 2];
        for (int i = 0; i < ret.Length; i++)
            ret[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return ret;
    }

    /// <summary>
    /// 被动模式（TCP 服务端）：对端发来触发内容，模拟器应自动把「发送内容」回发。
    /// 这是扫码枪模拟器最典型的用法 —— 真实扫码枪被上位机读到的就是这个回包。
    /// </summary>
    private static int CheckPassiveTriggerServer()
    {
        int fail = 0;
        Line("--- F: TCP 服务端 被动模式触发 ---");

        var comm = new CommTcpServer
        {
            TcpPort = 0,
            SendContent = "BARCODE-123",
            TriggerContent = "TRIGGER",
            IsVerifyTriggerContent = true,
            ReceiveContentType = EnumContentType.Text,
            WorkModel = EnumWorkModel.Passive,
            IsEnabled = true
        };

        try
        {
            Thread.Sleep(1200);
            int port = 0;
            var f = typeof(CommTcpServer).GetField("_server",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f?.GetValue(comm) is TcpListener lst)
                port = ((IPEndPoint)lst.LocalEndpoint).Port;
            if (port == 0) { fail++; Line("!! 未能取得监听端口"); return fail; }
            Line($"监听端口 = {port}");

            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            Thread.Sleep(600);

            var got = new TaskCompletionSource<string>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await client.GetStream().ReadAsync(buf, 0, buf.Length);
                    got.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { got.TrySetResult("EX:" + ex.Message); }
            });

            // 模拟上位机发来触发内容
            var trig = Encoding.UTF8.GetBytes("TRIGGER");
            client.GetStream().Write(trig, 0, trig.Length);
            client.GetStream().Flush();
            Line("已发送触发内容 \"TRIGGER\"");

            bool ok = got.Task.Wait(4000);
            string data = ok ? got.Task.Result : "(超时未收到回包)";
            Line($"收到回包 = \"{data}\"");
            if (!ok || !data.Contains("BARCODE-123"))
            {
                fail++;
                Line("!! 被动模式没有按触发内容回发");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            comm.Dispose();
        }
        return fail;
    }

    /// <summary>
    /// 假设：ReadWithTimeout 超时后把挂起的 ReadAsync 丢在后台，
    /// 下一轮又发起新的 ReadAsync —— 同一 NetworkStream 上出现并发读，
    /// .NET 会抛 InvalidOperationException（只允许一个未完成的读操作），
    /// 该异常被 DoClientWork 的 catch(Exception) 捕获 → client.Dispose() → 客户端被踢下线。
    /// </summary>
    private static int CheckConcurrentReadHypothesis()
    {
        int fail = 0;
        Line("--- H: ReadWithTimeout 残留任务导致并发读 ---");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            // 主动连向监听端，避免 AcceptTcpClient 阻塞等待
            var conn = new TcpClient();
            conn.Connect(IPAddress.Loopback, port);
            using var client = listener.AcceptTcpClient();
            var s = client.GetStream();

            // 复刻「旧写法」：ReadAsync + Wait，超时后把任务遗弃在流上
            int OldReadWithTimeout(Stream st, byte[] buffer, int timeoutMs)
            {
                try
                {
                    var readTask = st.ReadAsync(buffer, 0, buffer.Length);
                    if (!readTask.Wait(timeoutMs)) return 0;   // 超时：任务留在后台
                    return readTask.Result;
                }
                catch (AggregateException) { return 0; }
                catch (Exception) { return 0; }
            }

            var buf = new byte[4096];
            int r1 = OldReadWithTimeout(s, buf, 200);
            Line($"旧写法第 1 轮读 = {r1}（无数据，超时；ReadAsync 任务被遗弃在流上）");

            // 现在对端发来数据
            var payload = Encoding.UTF8.GetBytes("TRIGGER");
            conn.GetStream().Write(payload, 0, payload.Length);
            conn.GetStream().Flush();
            Line("对端已发送 \"TRIGGER\"");

            // 第二轮：用旧写法读 —— 数据应当被第 1 轮遗弃的任务吞掉
            var buf2 = new byte[4096];
            int r2 = OldReadWithTimeout(s, buf2, 200);
            Line($"旧写法第 2 轮读 = {r2}（期望 0 —— 证明数据被遗弃任务吞掉了）");
            if (r2 != 0)
            {
                fail++;
                Line("?? 旧写法第 2 轮竟然读到了，结论需重新评估");
            }
            else
            {
                Line("   证实：旧写法下数据被遗弃任务吞掉，后续永远读不到 → 无法触发");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            listener.Stop();
        }
        return fail;
    }

    /// <summary>
    /// 被动模式（TCP 客户端）：模拟器连上对端，对端发来触发内容后应自动回发。
    /// </summary>
    private static int CheckPassiveTriggerClient()
    {
        int fail = 0;
        Line("--- G: TCP 客户端 被动模式触发 ---");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            SendContent = "BARCODE-CLI",
            TriggerContent = "TRIGGER",
            IsVerifyTriggerContent = true,
            ReceiveContentType = EnumContentType.Text,
            WorkModel = EnumWorkModel.Passive,
            IsEnabled = true
        };

        try
        {
            var got = new TaskCompletionSource<string>();
            using var peer = listener.AcceptTcpClient();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    got.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { got.TrySetResult("EX:" + ex.Message); }
            });

            Thread.Sleep(1500);
            var trig = Encoding.UTF8.GetBytes("TRIGGER");
            peer.GetStream().Write(trig, 0, trig.Length);
            peer.GetStream().Flush();
            Line("已发送触发内容 \"TRIGGER\"");

            bool ok = got.Task.Wait(4000);
            string data = ok ? got.Task.Result : "(超时未收到回包)";
            Line($"收到回包 = \"{data}\"");
            if (!ok || !data.Contains("BARCODE-CLI"))
            {
                fail++;
                Line("!! 被动模式没有按触发内容回发");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            comm.Dispose();
            listener.Stop();
        }
        return fail;
    }

    /// <summary>
    /// 被动模式 + HEX 接收类型：端到端验证。
    /// 对端发来原始字节 80 FE 41（非 UTF-8 合法字节），触发内容配 80FE41，
    /// 应当能正常回发 —— 修复前这里永远匹配不上。
    /// </summary>
    private static int CheckPassiveHexTrigger()
    {
        int fail = 0;
        Line("--- K: 被动模式 + HEX 接收类型（端到端）---");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var comm = new CommTcpClient
        {
            TcpIP = "127.0.0.1",
            TcpPort = port,
            SendContent = "HEX-REPLY",
            TriggerContent = "80FE41",
            IsVerifyTriggerContent = true,
            ReceiveContentType = EnumContentType.Hex,
            WorkModel = EnumWorkModel.Passive,
            IsEnabled = true
        };

        try
        {
            var got = new TaskCompletionSource<string>();
            using var peer = listener.AcceptTcpClient();
            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n = await peer.GetStream().ReadAsync(buf, 0, buf.Length);
                    got.TrySetResult(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (Exception ex) { got.TrySetResult("EX:" + ex.Message); }
            });

            Thread.Sleep(1500);

            // 发送非 UTF-8 合法字节
            var trig = new byte[] { 0x80, 0xFE, 0x41 };
            peer.GetStream().Write(trig, 0, trig.Length);
            peer.GetStream().Flush();
            Line("已发送原始字节 [80 FE 41]，触发内容 = \"80FE41\"");

            bool ok = got.Task.Wait(4000);
            string data = ok ? got.Task.Result : "(超时未收到回包)";
            Line($"收到回包 = \"{data}\"");
            if (!ok || !data.Contains("HEX-REPLY"))
            {
                fail++;
                Line("!! HEX 模式被动触发没有回发");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            comm.Dispose();
            listener.Stop();
        }
        return fail;
    }

    /// <summary>
    /// 验证：对 NetworkStream 用 ReadAsync + CancellationToken 超时取消后，
    /// 连接是否还能继续使用（.NET 6 的已知行为：取消异步读可能使 socket 不可用）。
    /// </summary>
    private static int CheckCancelBreaksStream()
    {
        int fail = 0;
        Line("--- I: 取消异步读后流还能否继续使用 ---");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var conn = new TcpClient();
            conn.Connect(IPAddress.Loopback, port);
            using var client = listener.AcceptTcpClient();
            var s = client.GetStream();

            // 第一次：超时取消
            int r1;
            try
            {
                using var cts = new CancellationTokenSource(200);
                r1 = s.ReadAsync(new byte[4096].AsMemory(), cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { r1 = 0; }
            catch (Exception ex) { r1 = -1; Line($"第 1 次读异常: {ex.GetType().Name}"); }
            Line($"第 1 次读（超时取消）= {r1}");

            // 对端发数据
            var payload = Encoding.UTF8.GetBytes("TRIGGER");
            conn.GetStream().Write(payload, 0, payload.Length);
            conn.GetStream().Flush();

            // 第二次：应当能读到
            int r2;
            try
            {
                using var cts2 = new CancellationTokenSource(1500);
                r2 = s.ReadAsync(new byte[4096].AsMemory(), cts2.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { r2 = 0; }
            catch (Exception ex)
            {
                r2 = -1;
                Line($"第 2 次读异常: {ex.GetType().Name}: {ex.Message}");
            }
            Line($"第 2 次读 = {r2}（期望 > 0）");
            if (r2 <= 0)
            {
                fail++;
                Line("!! 取消异步读后流不可再用 —— 这就是刚才 G 用例反而失败的原因");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            listener.Stop();
        }
        return fail;
    }

    /// <summary>
    /// 验证：同步 Read + Socket.ReceiveTimeout 能否做到「有超时的读」，
    /// 且不产生僵尸读任务、不破坏流复用。
    /// </summary>
    private static int CheckSyncReadWithSocketTimeout()
    {
        int fail = 0;
        Line("--- J: 同步 Read + ReceiveTimeout ---");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var conn = new TcpClient();
            conn.Connect(IPAddress.Loopback, port);
            using var client = listener.AcceptTcpClient();
            client.ReceiveTimeout = 300;
            var s = client.GetStream();

            // 第 1 次：无数据，应当在超时后返回（抛异常被吞掉 → 0），且不阻塞过久
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int r1 = ReadSync(s, new byte[4096], client);
            sw.Stop();
            Line($"第 1 次读 = {r1}，耗时 {sw.ElapsedMilliseconds}ms（期望 0，耗时 ~300ms）");
            if (r1 != 0) { fail++; Line("!! 超时时不应有数据"); }
            if (sw.ElapsedMilliseconds > 2000) { fail++; Line("!! 超时生效但耗时过长"); }

            // 对端发数据
            var payload = Encoding.UTF8.GetBytes("TRIGGER");
            conn.GetStream().Write(payload, 0, payload.Length);
            conn.GetStream().Flush();

            // 第 2 次：应当读到
            sw.Restart();
            int r2 = ReadSync(s, new byte[4096], client);
            sw.Stop();
            Line($"第 2 次读 = {r2}，耗时 {sw.ElapsedMilliseconds}ms（期望 > 0）");
            if (r2 <= 0)
            {
                fail++;
                Line("!! 第 2 次读不到数据 —— 同步读方案不可用");
            }
            else
            {
                Line($"读到内容 = \"{Encoding.UTF8.GetString(new byte[4096], 0, r2)}\"");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Line("!! 异常: " + ex.Message);
        }
        finally
        {
            listener.Stop();
        }
        return fail;
    }

    /// <summary>复刻将要落地的实现：同步 Read，遵守 socket 的 ReceiveTimeout</summary>
    private static int ReadSync(Stream s, byte[] buffer, TcpClient owner)
    {
        try
        {
            return s.Read(buffer, 0, buffer.Length);
        }
        catch (IOException) { return 0; }      // 超时
        catch (SocketException) { return 0; }
        catch (Exception) { return 0; }
    }
}
