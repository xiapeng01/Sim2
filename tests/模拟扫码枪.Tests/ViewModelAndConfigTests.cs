using System.ComponentModel;
using System.IO;
using Newtonsoft.Json;

namespace 模拟扫码枪.Tests;

/// <summary>
/// 自动确认删除的 ViewModel，避免测试过程中弹出 MessageBox 阻塞进程。
/// </summary>
public class AutoConfirmViewModel : MainWindowViewModel
{
    public bool ConfirmResult { get; set; } = true;
    protected override bool ConfirmRemove(string name) => ConfirmResult;
}

/// <summary>
/// 配置持久化测试。继承 <see cref="TempDirTestBase"/> 在独立临时目录中运行，
/// 因为 MainWindowViewModel 使用相对路径 "setting.json"，依赖当前工作目录。
/// 归入 <see cref="WorkingDirectoryCollection"/> 禁止并行，避免相互干扰。
/// </summary>
[Collection(nameof(WorkingDirectoryCollection))]
public sealed class SettingPersistenceTests : TempDirTestBase
{
    [Fact]
    public void 保存后应生成setting文件()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        vm.ScannerList[0].Scanner.Name = "我的模拟器";

        Assert.True(vm.SaveSetting());
        Assert.True(SettingExists);
    }

    [Fact]
    public void 保存再加载数据应一致()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        var item = vm.ScannerList[0];
        item.Scanner.Name = "回环测试";
        item.Scanner.TcpIP = "10.20.30.40";
        item.Scanner.TcpPort = 6543;
        item.Scanner.SendContent = "HELLO";
        item.Scanner.WorkModel = EnumWorkModel.OnlySend;
        item.Scanner.EndOfFrame = EnumEndOfFrame.CR;
        item.Scanner.AppendContent = EnumAppendContent.Random;
        item.Scanner.AppendContentLength = 8;
        item.Scanner.IsVerifyTriggerContent = false;
        Assert.True(vm.SaveSetting());

        var reloaded = new MainWindowViewModel();
        Assert.Single(reloaded.ScannerList);
        var r = reloaded.ScannerList[0];
        Assert.Equal("回环测试", r.Scanner.Name);
        Assert.Equal("10.20.30.40", r.Scanner.TcpIP);
        Assert.Equal(6543, r.Scanner.TcpPort);
        Assert.Equal("HELLO", r.Scanner.SendContent);
        Assert.Equal(EnumWorkModel.OnlySend, r.Scanner.WorkModel);
        Assert.Equal(EnumEndOfFrame.CR, r.Scanner.EndOfFrame);
        Assert.Equal(EnumAppendContent.Random, r.Scanner.AppendContent);
        Assert.Equal(8, r.Scanner.AppendContentLength);
        Assert.False(r.Scanner.IsVerifyTriggerContent);
    }

    [Fact]
    public void 加载时内外层接口类型应一致()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        vm.AddItemCommand.Execute(null);
        vm.ScannerList[0].InterfaceType = EnumInterfaceType.TcpServer;
        vm.ScannerList[1].InterfaceType = EnumInterfaceType.TcpClient;
        Assert.True(vm.SaveSetting());

        var reloaded = new MainWindowViewModel();
        foreach (var w in reloaded.ScannerList)
        {
            Assert.Equal(w.InterfaceType, w.Scanner.InterfaceType);
        }
    }

    [Fact]
    public void 文件不存在时加载应失败但不抛异常()
    {
        var vm = new MainWindowViewModel();
        Assert.Empty(vm.ScannerList);
    }

    [Fact]
    public void 内容损坏时加载应被捕获()
    {
        WriteSetting("{ 这不是合法 JSON ");
        var vm = new MainWindowViewModel();
        Assert.Empty(vm.ScannerList);
        Assert.Contains(vm.Logs, l => l.Level == LogLevel.Error);
    }

    [Fact]
    public void 历史脏数据保存后应被修正()
    {
        // 构造一份外层与内层接口类型不一致的老配置
        var json = "[\n"
                 + "  {\n"
                 + "    \"InterfaceType\": 2,\n"
                 + "    \"Scanner\": {\n"
                 + "      \"$type\": \"模拟扫码枪.CommTcpServer, 模拟扫码枪\",\n"
                 + "      \"Name\": \"老配置\",\n"
                 + "      \"InterfaceType\": 0,\n"
                 + "      \"TcpPort\": 8000\n"
                 + "    }\n"
                 + "  }\n"
                 + "]";
        WriteSetting(json);

        var vm = new MainWindowViewModel();
        Assert.Single(vm.ScannerList);
        var w = vm.ScannerList[0];
        Assert.Equal(EnumInterfaceType.TcpServer, w.InterfaceType);
        Assert.Equal(w.InterfaceType, w.Scanner.InterfaceType);
        Assert.IsType<CommTcpServer>(w.Scanner);

        // 重新保存后 JSON 中应已一致
        Assert.True(vm.SaveSetting());
        var data = JsonConvert.DeserializeObject<List<Newtonsoft.Json.Linq.JObject>>(
            ReadSetting())!;
        var outer = (EnumInterfaceType)data[0]["InterfaceType"]!.ToObject<int>();
        var inner = (EnumInterfaceType)data[0]["Scanner"]!["InterfaceType"]!.ToObject<int>();
        Assert.Equal(outer, inner);
    }

    [Fact]
    public void 加载空数组应得到空列表()
    {
        WriteSetting("[]");
        var vm = new MainWindowViewModel();
        Assert.Empty(vm.ScannerList);
    }

    [Fact]
    public void 加载后应自动选中第一个模拟器()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        vm.AddItemCommand.Execute(null);
        vm.SaveSetting();

        var reloaded = new MainWindowViewModel();
        Assert.NotNull(reloaded.Scanner);
        Assert.Same(reloaded.ScannerList[0], reloaded.Scanner);
        Assert.True(reloaded.IsCanDelected);
    }
}

/// <summary>MainWindowViewModel 的集合与命令行为</summary>
[Collection(nameof(WorkingDirectoryCollection))]
public class MainWindowViewModelTests : TempDirTestBase
{
    [Fact]
    public void 新增后列表应增加且选中新项()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);

        Assert.Single(vm.ScannerList);
        Assert.Equal(1, vm.ScannerCount);
        Assert.Same(vm.ScannerList[0], vm.Scanner);
    }

    [Fact]
    public void 列表数量应随增删变化()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        vm.AddItemCommand.Execute(null);
        Assert.Equal(2, vm.ScannerCount);
    }

    [Fact]
    public void 删除后应重新选中第一项()
    {
        var vm = new AutoConfirmViewModel();
        vm.AddItemCommand.Execute(null);
        vm.AddItemCommand.Execute(null);

        vm.Scanner = vm.ScannerList[1];
        vm.RemoveItemCommand.Execute(null);

        Assert.Single(vm.ScannerList);
        Assert.Same(vm.ScannerList[0], vm.Scanner);
    }

    [Fact]
    public void 用户取消确认时不应删除()
    {
        var vm = new AutoConfirmViewModel { ConfirmResult = false };
        vm.AddItemCommand.Execute(null);
        vm.RemoveItemCommand.Execute(null);
        Assert.Single(vm.ScannerList);
    }

    [Fact]
    public void 未选中时不应允许删除()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        vm.Scanner = null;
        Assert.False(vm.IsCanDelected);
        Assert.False(vm.RemoveItemCommand.CanExecute(null));
    }

    [Fact]
    public void 切换接口类型后应仍可删除()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        vm.ScannerList[0].InterfaceType = EnumInterfaceType.TcpClient;
        Assert.True(vm.IsCanDelected);
    }

    [Fact]
    public void 清空日志应移除全部条目()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        Assert.NotEmpty(vm.Logs);

        vm.ClearLogsCommand.Execute(null);
        Assert.Empty(vm.Logs);
    }

    [Fact]
    public void 日志应包含级别信息()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        Assert.Contains(vm.Logs, l => l.Level == LogLevel.Success);
    }

    [Fact]
    public void DisposeAll不应抛异常()
    {
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        vm.AddItemCommand.Execute(null);
        var ex = Record.Exception(() => vm.DisposeAll());
        Assert.Null(ex);
    }

    [Fact]
    public void 自动滚动开关默认为开启()
    {
        var vm = new MainWindowViewModel();
        Assert.True(vm.AutoScroll);
    }

    [Fact]
    public void 日志条目应包含时间文本()
    {
        var entry = new LogEntry(DateTime.Now, LogLevel.Info, "内容");
        Assert.Equal("内容", entry.Text);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.False(string.IsNullOrWhiteSpace(entry.TimeText));
    }
}

/// <summary>BindingList 的 ListChanged 订阅（ScannerCount 刷新依赖它）</summary>
[Collection(nameof(WorkingDirectoryCollection))]
public class ScannerListNotificationTests : TempDirTestBase
{
    [Fact]
    public void 替换列表后增删应触发数量更新()
    {
        var vm = new MainWindowViewModel();
        int notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ScannerCount)) notifications++;
        };

        vm.ScannerList = new BindingList<ScannerWrap>();
        vm.ScannerList.Add(new ScannerWrap());
        vm.ScannerList.Add(new ScannerWrap());
        vm.ScannerList.RemoveAt(0);

        Assert.True(notifications > 0, "替换列表后 ListChanged 订阅应生效");
    }

    [Fact]
    public void 初始列表的增删也应刷新数量()
    {
        // 报告 P2 #16：字段初始化绕过了 setter，可能导致未订阅 ListChanged
        var vm = new MainWindowViewModel();
        vm.AddItemCommand.Execute(null);
        Assert.Equal(1, vm.ScannerCount);
    }
}
