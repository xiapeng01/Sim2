using System;
using System.IO;
using System.Windows;
using 模拟扫码枪;

namespace UiRenderTest;

/// <summary>
/// 功能回归测试：验证配置读写、接口切换保留参数、启用/禁用等关键行为。
/// 用法：UiRenderTest.exe test
/// </summary>
internal static class Regression
{
    private static int _fail;

    /// <summary>
    /// 本次回归专用的配置文件路径。注入给 ViewModel，
    /// 避免污染 exe 同级目录下的真实 setting.json，也避免受其内容干扰。
    /// </summary>
    private static string _settingFile = "";

    public static int Run(string[] args)
    {
        _fail = 0;
        var workDir = Path.Combine(Path.GetTempPath(), "扫码枪测试");
        Directory.CreateDirectory(workDir);
        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        _settingFile = Path.Combine(workDir, "regression-setting.json");
        if (File.Exists(_settingFile)) File.Delete(_settingFile);

        try
        {
            TestRoundTrip();
            TestInterfaceSwitchKeepsSettings();
            TestEnableDisable();
            TestEnumDescriptions();
            _fail += RealConfigCheck.Run(""); // 内部自动定位仓库中的真实 setting.json
            _fail += P0ResilienceCheck.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            _fail++;
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
        }

        Console.WriteLine(_fail == 0 ? "\nALL TESTS PASSED" : $"\n{_fail} TEST(S) FAILED");
        return _fail == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  [FAIL] {name}" + (detail is null ? "" : $" -> {detail}"));
        }
    }

    /// <summary>配置保存后再加载，数据应保持一致</summary>
    private static void TestRoundTrip()
    {
        Console.WriteLine("\n[1] 配置保存 / 加载往返");
        var file = _settingFile;
        if (File.Exists(file)) File.Delete(file);

        var vm = new MainWindowViewModel(file);
        vm.AddItemCommand.Execute(null);
        var wrap = vm.ScannerList[0];
        wrap.Scanner.Name = "测试机";
        wrap.Scanner.TcpIP = "192.168.1.100";
        wrap.Scanner.TcpPort = 9527;
        wrap.Scanner.SendContent = "TESTPAYLOAD";
        wrap.InterfaceType = EnumInterfaceType.TcpClient;

        Check("保存返回 true", vm.SaveSetting());
        Check("setting.json 已生成", File.Exists(file));

        var vm2 = new MainWindowViewModel(file);
        Check("重新加载得到 1 个模拟器", vm2.ScannerList.Count == 1, $"实际 {vm2.ScannerList.Count}");
        if (vm2.ScannerList.Count == 1)
        {
            var w = vm2.ScannerList[0];
            Check("名称已恢复", w.Scanner.Name == "测试机", w.Scanner.Name);
            Check("IP 已恢复", w.Scanner.TcpIP == "192.168.1.100", w.Scanner.TcpIP);
            Check("端口已恢复", w.Scanner.TcpPort == 9527, w.Scanner.TcpPort.ToString());
            Check("发送内容已恢复", w.Scanner.SendContent == "TESTPAYLOAD", w.Scanner.SendContent);
            Check("接口类型已恢复", w.InterfaceType == EnumInterfaceType.TcpClient, w.InterfaceType.ToString());
            Check("内层接口类型与外层一致",
                  w.Scanner.InterfaceType == w.InterfaceType,
                  $"内层 {w.Scanner.InterfaceType} / 外层 {w.InterfaceType}");
        }
    }

    /// <summary>切换接口类型时，已填写的参数不应丢失</summary>
    private static void TestInterfaceSwitchKeepsSettings()
    {
        Console.WriteLine("\n[2] 切换接口类型保留参数");
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        var wrap = vm.ScannerList[0];
        wrap.Scanner.Name = "保留测试";
        wrap.Scanner.TcpIP = "10.0.0.5";
        wrap.Scanner.TcpPort = 1234;
        wrap.Scanner.SendContent = "KEEPME";
        wrap.Scanner.TriggerContent = "TRIG";
        wrap.Scanner.AppendContentLength = 9;

        wrap.InterfaceType = EnumInterfaceType.TcpServer;
        Check("切换后仍是 TcpServer", wrap.InterfaceType == EnumInterfaceType.TcpServer);
        Check("名称保留", wrap.Scanner.Name == "保留测试", wrap.Scanner.Name);
        Check("IP 保留", wrap.Scanner.TcpIP == "10.0.0.5", wrap.Scanner.TcpIP);
        Check("端口保留", wrap.Scanner.TcpPort == 1234, wrap.Scanner.TcpPort.ToString());
        Check("发送内容保留", wrap.Scanner.SendContent == "KEEPME", wrap.Scanner.SendContent);
        Check("触发内容保留", wrap.Scanner.TriggerContent == "TRIG", wrap.Scanner.TriggerContent);
        Check("附加长度保留", wrap.Scanner.AppendContentLength == 9, wrap.Scanner.AppendContentLength.ToString());
        Check("内层对象类型匹配", wrap.Scanner is CommTcpServer, wrap.Scanner.GetType().Name);
    }

    /// <summary>启用/禁用后应能再次恢复工作（修复 Dispose 导致的永久失效）</summary>
    private static void TestEnableDisable()
    {
        Console.WriteLine("\n[3] 启用/禁用可反复切换");
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        var wrap = vm.ScannerList[0];
        wrap.Scanner.Name = "生命周期";

        wrap.Scanner.IsEnabled = true;
        Thread.Sleep(120);
        Check("启用后工作循环仍然存活", !IsDisposed(wrap.Scanner));

        wrap.Scanner.IsEnabled = false;
        Thread.Sleep(120);
        wrap.Scanner.IsEnabled = true;
        Thread.Sleep(120);
        Check("禁用再启用后仍可工作", !IsDisposed(wrap.Scanner));
    }

    private static bool IsDisposed(AComm comm)
    {
        var field = typeof(AComm).GetField("IsDisposed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field is not null && field.GetValue(comm) is true;
    }

    /// <summary>枚举应有中文说明，界面才能显示可读文本</summary>
    private static void TestEnumDescriptions()
    {
        Console.WriteLine("\n[4] 枚举中文说明");
        var conv = new EnumDescriptionConverter();
        Check("串口 -> 串口", (string)conv.Convert(EnumInterfaceType.SerialPort, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) == "串口");
        Check("工作模式 -> 主动触发", (string)conv.Convert(EnumWorkModel.Trigger, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) == "主动触发");

        var vis = new EnumToVisibilityConverter();
        Check("TcpClient 匹配 TcpClient 可见",
            IsVisible(Vis(vis, EnumInterfaceType.TcpClient, "TcpClient,TcpServer")), "应为 Visible");
        Check("SerialPort 不匹配 TcpClient 折叠",
            !IsVisible(Vis(vis, EnumInterfaceType.SerialPort, "TcpClient,TcpServer")), "应为 Collapsed");
        Check("取反：OnlyReceive 时 !OnlyReceive 折叠",
            !IsVisible(Vis(vis, EnumWorkModel.OnlyReceive, "!OnlyReceive")), "应为 Collapsed");
        Check("取反：Trigger 时 !OnlyReceive 可见",
            IsVisible(Vis(vis, EnumWorkModel.Trigger, "!OnlyReceive")), "应为 Visible");
    }

    private static bool IsVisible(object? result) => result is Visibility.Visible;

    private static object? Vis(EnumToVisibilityConverter conv, object value, string parameter)
        => conv.Convert(value, typeof(Visibility), parameter, System.Globalization.CultureInfo.InvariantCulture);
}
