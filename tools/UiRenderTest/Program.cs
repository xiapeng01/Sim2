using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UiRenderTest;

/// <summary>
/// 离屏渲染主窗口，用于验证 XAML 能否正确实例化与布局，并输出 PNG 供检查。
/// 用法：UiRenderTest.exe render &lt;输出目录&gt;
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "render";
        var outDir = args.Length > 1 ? args[1] : ".";

        if (mode == "render") return Render(outDir);
        if (mode == "combo") return ComboCheck.Run(outDir);
        if (mode == "trigger") return TriggerCheck.Run();
        return Regression.Run(args);
    }

    private static int Render(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // 使用真实的 App 以便加载 App.xaml 中定义的主题资源
        var app = new 模拟扫码枪.App();
        app.InitializeComponent();

        try
        {
            var window = new 模拟扫码枪.MainWindow
            {
                Width = 1180,
                Height = 720
            };
            window.Show();

            Pump();
            window.UpdateLayout();
            Pump();

            RenderToFile(window, Path.Combine(outDir, "01-empty.png"));
            Console.WriteLine("rendered 01-empty.png");

            // 造两个模拟器，验证列表卡片与属性面板的填充态
            if (window.DataContext is 模拟扫码枪.MainWindowViewModel vm)
            {
                if (vm.ScannerList.Count == 0)
                {
                    vm.AddItemCommand.Execute(null);
                    vm.AddItemCommand.Execute(null);
                }
                if (vm.ScannerList.Count > 1)
                {
                    vm.ScannerList[0].Scanner.Name = "串口模拟器";
                    vm.ScannerList[1].Scanner.Name = "TCP服务端";
                    vm.ScannerList[1].InterfaceType = 模拟扫码枪.EnumInterfaceType.TcpServer;
                    vm.Scanner = vm.ScannerList[1];
                }
            }
            Pump();
            window.UpdateLayout();
            Pump();

            RenderToFile(window, Path.Combine(outDir, "02-populated.png"));
            Console.WriteLine("rendered 02-populated.png");

            // 切到"运行日志"页签再渲染一次
            var tabControl = FindTabControl(window);
            if (tabControl is not null && tabControl.Items.Count > 1)
            {
                tabControl.SelectedIndex = 1;
                Pump();
                window.UpdateLayout();
                Pump();
                RenderToFile(window, Path.Combine(outDir, "03-logs.png"));
                Console.WriteLine("rendered 03-logs.png");
            }
            else
            {
                Console.WriteLine("WARN: tab control not found");
            }

            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
            return 1;
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 12; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void RenderToFile(Visual visual, string path)
    {
        var size = new Size(visual is FrameworkElement fe && fe.ActualWidth > 0 ? fe.ActualWidth : 1180,
                            visual is FrameworkElement fe2 && fe2.ActualHeight > 0 ? fe2.ActualHeight : 720);
        var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static System.Windows.Controls.TabControl? FindTabControl(DependencyObject root)
    {
        if (root is System.Windows.Controls.TabControl tc) return tc;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindTabControl(child);
            if (found is not null) return found;
        }
        return null;
    }
}
