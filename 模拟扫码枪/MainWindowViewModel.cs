using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

    /// <summary>运行日志的最大条数，超出后丢弃最旧的记录。</summary>
    /// <remarks>
    /// TCP 客户端在对端未启动时每 2 秒重试一次并写一条日志，
    /// 若不设上限，长时间运行会让集合无限膨胀（ListBox 虽然开了虚拟化，
    /// 但集合本身仍全量驻留内存）。
    /// </remarks>
    public const int MaxLogCount = 1000;

    public bool IsCanDelected => Scanner is not null && ScannerList.Contains(Scanner);

    /// <summary>模拟器数量（列表增删时需要主动通知）</summary>
    public int ScannerCount => ScannerList.Count;

    /// <summary>
    /// 配置文件路径。
    /// <para>
    /// 原先用相对路径 "setting.json"，依赖进程当前工作目录 ——
    /// 从快捷方式启动时工作目录是 exe 所在目录，从别处用 <c>dotnet run</c> 启动时
    /// 又是项目目录，配置文件会散落在不同地方，表现为「明明保存了，重启后配置却没了」。
    /// 这里默认固定到 exe 同级目录。
    /// </para>
    /// <para>
    /// 路径做成可注入，是为了让单元测试能各自使用独立的配置文件，
    /// 否则同一进程内的多个实例会互相覆盖对方的配置。
    /// </para>
    /// </summary>
    public string SettingFilePath { get; }

    /// <summary>
    /// 无参构造函数。XAML 中以 <c>&lt;local:MainWindowViewModel/&gt;</c> 声明 DataContext 时
    /// 必须有无参构造函数——WPF 的 XAML 加载器无法解析带默认参数的构造函数
    /// （会抛 NullReferenceException: DefaultBinder.BindToMethod）。
    /// </summary>
    public MainWindowViewModel() : this(null)
    {
    }

    /// <summary>
    /// 指定配置文件路径的构造函数。测试用隔离路径，避免多个用例互相覆盖配置。
    /// </summary>
    public MainWindowViewModel(string? settingFilePath)
    {
        SettingFilePath = string.IsNullOrWhiteSpace(settingFilePath)
            ? DefaultSettingFilePath
            : settingFilePath!;
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
            AppendLog(entry);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => AppendLog(entry)));
        }
    }

    /// <summary>追加一条日志并维持集合长度上限</summary>
    void AppendLog(LogEntry entry)
    {
        Logs.Add(entry);
        // 超出上限时丢弃最旧的记录，防止集合无限膨胀
        while (Logs.Count > MaxLogCount)
        {
            Logs.RemoveAt(0);
        }
    }

    /// <summary>
    /// 配置文件路径。默认固定到 exe 同级目录，单元测试可注入各自的隔离路径。
    /// </summary>
    static string DefaultSettingFilePath => Path.Combine(AppContext.BaseDirectory, "setting.json");

    JsonSerializerSettings settings = new JsonSerializerSettings()
    {
        TypeNameHandling = TypeNameHandling.Auto,//序列化时包含类型信息，反序列化时根据类型信息创建对象
        // 安全加固：只允许反序列化本程序集内的类型，
        // 避免配置文件被篡改后实例化任意类型（Newtonsoft 的已知攻击面）。
        SerializationBinder = new WhitelistSerializationBinder(),
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
            File.WriteAllText(SettingFilePath, jsonString);
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
            if (!File.Exists(SettingFilePath))
            {
                AddMessage("设置文件不存在，加载默认设置", LogLevel.Warning);
                return false;
            }
            var jsonString = File.ReadAllText(SettingFilePath);

            // 逐项反序列化：数组中途失败时，前面已创建出来的 ScannerWrap
            // （及其构造函数启动的后台任务、串口/Socket 句柄）必须立即释放，
            // 否则会成为永远无法回收的泄漏点，重试一次泄漏一批。
            var token = JToken.Parse(jsonString);
            if (token is not JArray array)
            {
                AddMessage("设置文件内容无效，加载默认设置", LogLevel.Error);
                return false;
            }

            var list = new BindingList<ScannerWrap>();
            int index = 0;
            foreach (var item in array)
            {
                index++;
                ScannerWrap? wrap = null;
                try
                {
                    wrap = item.ToObject<ScannerWrap>(JsonSerializer.Create(settings));
                    if (wrap is null) continue;
                    list.Add(wrap);
                }
                catch (Exception ex)
                {
                    // 该项失败不影响其它项，但已创建出来的对象必须释放
                    wrap?.Dispose();
                    AddMessage($"加载第 {index} 个模拟器失败：{ex.Message}", LogLevel.Error);
                }
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

/// <summary>
/// 反序列化白名单绑定器：只允许创建本程序集（模拟扫码枪）中的类型。
/// <para>
/// <c>TypeNameHandling.Auto</c> 会按配置里的 <c>$type</c> 字段反射创建任意类型，
/// 若配置文件被篡改或来自不可信来源，理论上可实例化任意类型。
/// 本工具是本地桌面程序风险有限，但这是行业公认的已知攻击面，用白名单堵上。
/// </para>
/// </summary>
public class WhitelistSerializationBinder : Newtonsoft.Json.Serialization.ISerializationBinder
{
    static readonly Assembly AllowedAssembly = typeof(WhitelistSerializationBinder).Assembly;

    /// <summary>
    /// 序列化时生成类型名。保持 Newtonsoft 默认的「命名空间.类型, 程序集名」格式，
    /// 保证与旧版本生成的配置文件完全兼容。
    /// </summary>
    public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
    {
        if (serializedType.Assembly != AllowedAssembly)
        {
            throw new JsonSerializationException($"不允许序列化程序集 '{serializedType.Assembly.GetName().Name}' 中的类型");
        }
        assemblyName = serializedType.Assembly.GetName().Name;
        typeName = serializedType.FullName;
    }

    /// <summary>
    /// 反序列化时解析类型名。只允许本程序集内的类型。
    /// </summary>
    public Type BindToType(string? assemblyName, string typeName)
    {
        var type = Type.GetType($"{typeName}, {assemblyName}", false);
        if (type is null)
        {
            throw new JsonSerializationException($"配置文件中的类型 '{typeName}' 不存在");
        }
        if (type.Assembly != AllowedAssembly)
        {
            throw new JsonSerializationException($"不允许反序列化程序集 '{assemblyName}' 中的类型 '{typeName}'");
        }
        return type;
    }
}
