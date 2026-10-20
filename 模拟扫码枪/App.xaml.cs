using System;
using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace 模拟扫码枪;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常处理：原先项目里没有任何未捕获异常的处理，
        // UI 线程上任何异常都会直接让进程闪退，用户只看到「程序无响应」，
        // 什么线索都没有。这里至少记录下来并给出提示。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    /// <summary>UI 线程未捕获异常</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandled("界面异常", e.Exception);
        e.Handled = true; // 标记为已处理，避免进程直接崩溃
        MessageBox.Show(
            $"发生未处理的异常：\n\n{e.Exception.Message}\n\n详细信息已记录到日志文件。",
            "模拟扫码枪", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>后台线程未捕获异常（此时进程通常已无法挽救）</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogUnhandled("后台异常", ex);
        }
    }

    /// <summary>把未捕获异常写入日志文件</summary>
    private static void LogUnhandled(string tag, Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}\r\n{ex}\r\n\r\n");
        }
        catch
        {
            // 写日志失败不能再抛异常
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 标记正在退出：此后所有向 UI 线程的投递都会被跳过，
        // 避免 Dispatcher 已关闭时 BeginInvoke 抛未捕获异常。
        AComm.IsShuttingDown = true;
        base.OnExit(e);
    }
}
