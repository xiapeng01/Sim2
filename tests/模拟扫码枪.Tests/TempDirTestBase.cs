using System.IO;
using 模拟扫码枪;

namespace 模拟扫码枪.Tests;

/// <summary>
/// 为每个测试提供独立的工作目录与独立的配置文件路径。
///
/// <para>
/// 背景：<c>MainWindowViewModel</c> 的配置文件路径原先是相对路径 <c>"setting.json"</c>，
/// 依赖进程的当前工作目录，而 xUnit 默认并行执行不同测试类，
/// 若多个测试同时切换目录就会互相干扰（表现为"单跑通过、全跑失败"）。
/// </para>
///
/// <para>
/// 现在配置路径已改为绝对路径（默认在 exe 同级目录），但同一进程内的多个
/// <c>MainWindowViewModel</c> 实例仍会共用同一个文件而互相污染，
/// 因此这里通过构造函数注入各自的隔离路径。
/// </para>
///
/// <para>
/// 本基类在构造函数中先把进程目录固定到一个专用锚点目录再切走，
/// 各测试只在自己的 <see cref="AnchorDir"/> 中读写互不影响；
/// <see cref="Dispose"/> 由 xUnit 在每个用例结束后自动调用。
/// </para>
/// </summary>
public abstract class TempDirTestBase : IDisposable
{
    /// <summary>进程级共享的"锚点"目录，用于承接并锁定那些仍依赖 CWD 的相对路径操作</summary>
    private static readonly string AnchorRoot =
        Path.Combine(Path.GetTempPath(), "扫码枪单测", "_anchor");

    protected TempDirTestBase()
    {
        // 先切到稳定目录，避免后续 SetCurrentDirectory 失败（目录已被删除）
        Directory.CreateDirectory(AnchorRoot);
        _prevDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(AnchorRoot);

        // 每个用例一个独立子目录
        AnchorDir = Path.Combine(AnchorRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AnchorDir);
        Directory.SetCurrentDirectory(AnchorDir);
    }

    private readonly string _prevDir;

    /// <summary>本用例独占的工作目录</summary>
    protected string AnchorDir { get; }

    /// <summary>本用例独占的配置文件路径（注入给 ViewModel）</summary>
    protected string SettingFilePath => Path.Combine(AnchorDir, "setting.json");

    /// <summary>创建一个使用本用例隔离配置文件的 ViewModel</summary>
    protected MainWindowViewModel CreateViewModel() => new(SettingFilePath);

    public void Dispose()
    {
        try
        {
            Directory.SetCurrentDirectory(_prevDir);
        }
        catch
        {
            // 目录可能已被清理，忽略
        }

        try
        {
            if (Directory.Exists(AnchorDir)) Directory.Delete(AnchorDir, true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>把配置文件写入本用例的隔离目录</summary>
    protected void WriteSetting(string json)
        => File.WriteAllText(SettingFilePath, json);

    /// <summary>读取本用例隔离目录中的配置文件</summary>
    protected string ReadSetting()
        => File.ReadAllText(SettingFilePath);

    protected bool SettingExists => File.Exists(SettingFilePath);
}

/// <summary>
/// 把依赖当前工作目录的测试归入同一集合，禁止并行执行。
/// 与 <see cref="TempDirTestBase"/> 配合使用。
/// </summary>
[CollectionDefinition(nameof(WorkingDirectoryCollection), DisableParallelization = true)]
public class WorkingDirectoryCollection
{
}
