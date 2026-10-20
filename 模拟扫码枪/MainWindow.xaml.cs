using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace 模拟扫码枪;

/// <summary>
/// Interaction logic for MainWindow
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (DataContext is MainWindowViewModel vm)
        {
            vm.Logs.CollectionChanged += OnLogsChanged;
        }
    }

    /// <summary>日志新增时自动滚动到底部</summary>
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.AutoScroll) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (vm.Logs.Count == 0) return;
            var last = vm.Logs[vm.Logs.Count - 1];
            LogList.ScrollIntoView(last);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 标记正在退出：此后通信线程不再向已关闭的 Dispatcher 投递
        AComm.IsShuttingDown = true;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SaveSetting();
            // 退出前释放所有模拟器，避免串口句柄 / Socket / 监听端口被占用
            vm.DisposeAll();
        }
    }
}
