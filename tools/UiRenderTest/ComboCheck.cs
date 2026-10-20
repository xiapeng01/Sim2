using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using 模拟扫码枪;

namespace UiRenderTest;

/// <summary>
/// ComboBox 显示异常专项验证。
///
/// <para>复现场景：属性面板的 ComboBox 用 SelectedItem 双向绑定到源属性。
/// 当源值不在 ItemsSource 列表中时，WPF 会把 SelectedItem 置为 null，
/// 模板只显示 SelectionBoxItem，界面就变成空白。</para>
///
/// <para>验证方式：人为把串口号/波特率设成「不在标准列表中」的值，
/// 渲染属性面板，断言 ComboBox 的显示文本与 SelectedItem 都非空。</para>
///
/// <para>用法：UiRenderTest.exe combo &lt;输出目录&gt;</para>
/// </summary>
internal static class ComboCheck
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var fail = 0;

        var app = new App();
        app.InitializeComponent();

        try
        {
            var window = new MainWindow { Width = 1180, Height = 720 };
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();

            if (window.DataContext is not MainWindowViewModel vm)
            {
                Console.WriteLine("FAIL: DataContext 不是 MainWindowViewModel");
                return 1;
            }

            if (vm.ScannerList.Count == 0) vm.AddItemCommand.Execute(null);
            var wrap = vm.ScannerList[0];
            vm.Scanner = wrap;

            // 关键：设成系统里几乎不可能存在的串口号 + 非标准波特率
            wrap.Scanner.SerialPortName = "COM99";
            wrap.Scanner.SerialPortBaudRate = 12345;

            Pump();
            window.UpdateLayout();
            Pump();

            var combos = FindComboBoxes(window).ToList();
            Console.WriteLine($"找到 {combos.Count} 个 ComboBox");

            // 定位串口、波特率、数据位三个 ComboBox
            var portCombo = combos.FirstOrDefault(c => FindBoundPath(c).EndsWith("SerialPortName"));
            var baudCombo = combos.FirstOrDefault(c => FindBoundPath(c).EndsWith("SerialPortBaudRate"));
            var bitsCombo = combos.FirstOrDefault(c => FindBoundPath(c).EndsWith("SerialPortDataBits"));

            fail += Check("串口号 ComboBox 存在", portCombo is not null);
            fail += Check("波特率 ComboBox 存在", baudCombo is not null);
            fail += Check("数据位 ComboBox 存在", bitsCombo is not null);

            if (portCombo is not null)
            {
                fail += Check("串口号 SelectedItem 非空", portCombo.SelectedItem is not null,
                    $"实际 = {portCombo.SelectedItem ?? "(null)"}");
                fail += Check("串口号显示文本非空", !string.IsNullOrEmpty(portCombo.Text),
                    $"实际 = '{portCombo.Text}'");
                fail += Check("串口号显示为 COM99", portCombo.Text == "COM99",
                    $"实际 = '{portCombo.Text}'");
            }

            if (baudCombo is not null)
            {
                fail += Check("波特率 SelectedItem 非空", baudCombo.SelectedItem is not null,
                    $"实际 = {baudCombo.SelectedItem ?? "(null)"}");
                fail += Check("波特率显示文本非空", !string.IsNullOrEmpty(baudCombo.Text),
                    $"实际 = '{baudCombo.Text}'");
                fail += Check("波特率显示为 12345", baudCombo.Text == "12345",
                    $"实际 = '{baudCombo.Text}'");
                fail += Check("波特率源值未丢失", wrap.Scanner.SerialPortBaudRate == 12345,
                    $"实际 = {wrap.Scanner.SerialPortBaudRate}");
            }

            if (bitsCombo is not null)
            {
                fail += Check("数据位 SelectedItem 非空", bitsCombo.SelectedItem is not null,
                    $"实际 = {bitsCombo.SelectedItem ?? "(null)"}");
                fail += Check("数据位显示文本非空", !string.IsNullOrEmpty(bitsCombo.Text),
                    $"实际 = '{bitsCombo.Text}'");
            }

            // 渲染属性面板区域，便于人工核对
            var path = Path.Combine(outDir, "04-combo-not-in-list.png");
            RenderToFile(window, path);
            Console.WriteLine("rendered " + Path.GetFileName(path));

            window.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL: " + ex);
            fail++;
        }

        Console.WriteLine(fail == 0 ? "\nCOMBO CHECK PASSED" : $"\n{fail} COMBO CHECK(S) FAILED");
        return fail == 0 ? 0 : 1;
    }

    private static int Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            Console.WriteLine("PASS: " + name);
            return 0;
        }
        Console.WriteLine("FAIL: " + name + (detail != null ? " -> " + detail : ""));
        return 1;
    }

    /// <summary>读取 ComboBox 上 SelectedItem 绑定的路径名</summary>
    private static string FindBoundPath(ComboBox combo)
    {
        var expr = combo.GetBindingExpression(ComboBox.SelectedItemProperty);
        return expr?.ParentBinding.Path.Path ?? "";
    }

    private static IEnumerable<ComboBox> FindComboBoxes(DependencyObject root)
    {
        if (root is ComboBox cb) yield return cb;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            foreach (var found in FindComboBoxes(VisualTreeHelper.GetChild(root, i)))
                yield return found;
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

    private static double GetWidth(Visual visual)
    {
        var fv = visual as FrameworkElement;
        return fv != null && fv.ActualWidth > 0 ? fv.ActualWidth : 1180;
    }

    private static double GetHeight(Visual visual)
    {
        var fv = visual as FrameworkElement;
        return fv != null && fv.ActualHeight > 0 ? fv.ActualHeight : 720;
    }

    private static void RenderToFile(Visual visual, string path)
    {
        var size = new Size(GetWidth(visual), GetHeight(visual));
        var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
