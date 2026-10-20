using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;

namespace 模拟扫码枪;

public partial class MainWindowViewModel : ObservableObject
{
    [NotifyCanExecuteChangedFor(nameof(RemoveItemCommand))]
    [NotifyPropertyChangedFor(nameof(IsCanDelected))]
    [NotifyPropertyChangedFor(nameof(ScannerCount))]
    [ObservableProperty] private ScannerWrap? _scanner;
    [ObservableProperty] private BindingList<ScannerWrap> _scannerList = new();
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>运行日志</summary>
    public ObservableCollection<LogEntry> Logs { get; } = new();

    public bool IsCanDelected => Scanner is not null && ScannerList.Contains(Scanner);

    /// <summary>模拟器数量（列表增删时需要主动通知）</summary>
    public int ScannerCount => ScannerList.Count;

    public MainWindowViewModel()
    {
        LoadSetting();
    }

    /// <summary>
    /// 释放全部模拟器。应用退出时调用，避免串口句柄、Socket、监听端口被占用到进程结束。
    /// </summary>
    public void DisposeAll()
    {
        foreach (var item in ScannerList)
        {
            try
            {
                UnhookScanner(item);
                item.Dispose();
            }
            catch (Exception ex)
            {
                AddMessage($"释放模拟器失败：{ex.Message}", LogLevel.Error);
            }
        }
    }

    /// <summary>列表被整体替换时重新订阅增删事件</summary>
    partial void OnScannerListChanged(BindingList<ScannerWrap> value)
    {
        if (_scannerListSubscribed is not null) _scannerListSubscribed.ListChanged -= OnScannerListItemChanged;
        _scannerListSubscribed = value;
        if (_scannerListSubscribed is not null) _scannerListSubscribed.ListChanged += OnScannerListItemChanged;
        OnPropertyChanged(nameof(ScannerCount));
    }

    BindingList<ScannerWrap>? _scannerListSubscribed;

    void OnScannerListItemChanged(object? sender, System.ComponentModel.ListChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ScannerCount));
        OnPropertyChanged(nameof(IsCanDelected));
    }

    [RelayCommand]
    void AddItem()
    {
        var sc = new ScannerWrap();
        HookScanner(sc);
        ScannerList.Add(sc);
        Scanner = sc;
        AddMessage($"已新增模拟器 [{sc.Scanner.Name}]", LogLevel.Success);
    }

    [RelayCommand(CanExecute = nameof(IsCanDelected))]
    void RemoveItem()
    {
        if (Scanner is null || Scanner.Scanner is null) return;
        if (!ConfirmRemove(Scanner.Scanner.Name)) return;
        if (ScannerList.Contains(Scanner))
        {
            UnhookScanner(Scanner);
            ScannerList.Remove(Scanner);
            AddMessage($"已删除模拟器 [{Scanner.Scanner.Name}]", LogLevel.Warning);
            // 必须调用 ScannerWrap.Dispose()，它会释放底层通信资源（串口/Socket/端口）
            // 并停止包装层自己的后台任务。只调用 Scanner.Scanner.Dispose() 会漏掉后者。
            Scanner.Dispose();
            Scanner = ScannerList.Count > 0 ? ScannerList[0] : null;
        }
    }

    /// <summary>
    /// 删除确认钩子。生产环境弹出模态对话框；单元测试可继承并重写为自动确认，
    /// 避免 MessageBox 阻塞测试进程。
    /// </summary>
    protected virtual bool ConfirmRemove(string name)
        => MessageBox.Show($"是否删除项[{name}]", "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    [RelayCommand]
    void SaveAllItems()
    {
        try
        {
            SaveSetting();
            AddMessage("保存成功", LogLevel.Success);
        }
        catch (Exception ex)
        {
            AddMessage($"保存失败：{ex.Message}", LogLevel.Error);
        }
    }

    [RelayCommand]
    void ClearLogs()
    {
        Logs.Clear();
    }

    /// <summary>挂接模拟器的消息/异常事件</summary>
    void HookScanner(ScannerWrap wrap)
    {
        if (wrap is null) return;
        wrap.Scanner.UpdateMessageEvent += AddMessage;
        wrap.Scanner.UpdateExceptionEvent += AddException;
        // 接口类型切换后底层对象会被重建，需要重新挂接
        wrap.ScannerReplaced += OnScannerReplaced;
    }

    void UnhookScanner(ScannerWrap wrap)
    {
        if (wrap is null) return;
        wrap.Scanner.UpdateMessageEvent -= AddMessage;
        wrap.Scanner.UpdateExceptionEvent -= AddException;
        wrap.ScannerReplaced -= OnScannerReplaced;
    }

    void OnScannerReplaced(object? sender, EventArgs e)
    {
        if (sender is not ScannerWrap wrap) return;
        wrap.Scanner.UpdateMessageEvent += AddMessage;
        wrap.Scanner.UpdateExceptionEvent += AddException;
    }
    void AddMessage(string msg) => AddMessage(msg, LogLevel.Info);

    void AddException(string msg) => AddMessage(msg, LogLevel.Error);

    void AddMessage(string msg, LogLevel level)
    {
        // 通信线程可能非 UI 线程，统一切回 UI 线程更新集合
        var entry = new LogEntry(DateTime.Now, level, msg);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Logs.Add(entry);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => Logs.Add(entry)));
        }
    }

    string settingFileName = "setting.json";

    JsonSerializerSettings settings = new JsonSerializerSettings()
    {
        TypeNameHandling = TypeNameHandling.Auto,//序列化时包含类型信息，反序列化时根据类型信息创建对象
        PreserveReferencesHandling = PreserveReferencesHandling.Objects,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,//处理循环引用，忽略循环引用的属性，避免序列化失败
        ObjectCreationHandling = ObjectCreationHandling.Replace,//创建列表对象时替换对象而不是附加内容，确保反序列化时列表被正确替换
        //PreserveReferencesHandling = PreserveReferencesHandling.None,//不保留对象引用，避免生成$ref和$id属性，简化JSON结构
        Formatting = Formatting.Indented//格式化输出，便于阅读
    };

    public bool SaveSetting()
    {
        try
        {
            var jsonString = JsonConvert.SerializeObject(ScannerList, settings);
            File.WriteAllText(settingFileName, jsonString);
            return true;
        }
        catch (Exception ex)
        {
            AddMessage($"保存设置失败：{ex.Message}", LogLevel.Error);
            return false;
        }
    }

    public bool LoadSetting()
    {
        try
        {
            if (!File.Exists(settingFileName))
            {
                AddMessage("设置文件不存在，加载默认设置", LogLevel.Warning);
                return false;
            }
            var jsonString = File.ReadAllText(settingFileName);
            var list = JsonConvert.DeserializeObject<BindingList<ScannerWrap>>(jsonString, settings);
            if (list is null)
            {
                AddMessage("设置文件内容无效，加载默认设置", LogLevel.Error);
                return false;
            }
            ScannerList = list;
            foreach (var item in list)
            {
                HookScanner(item);
            }
            if (ScannerList.Count > 0) Scanner = ScannerList[0];
            AddMessage($"加载设置成功，共 {ScannerList.Count} 个模拟器", LogLevel.Success);
            return true;
        }
        catch (Exception ex)
        {
            AddMessage($"加载设置失败：{ex.Message}", LogLevel.Error);
            return false;
        }
    }
}
