using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;

namespace 模拟扫码枪;

public partial class MainWindowViewModel:ObservableObject
{
    [NotifyCanExecuteChangedFor(nameof(RemoveItemCommand))]
    [NotifyPropertyChangedFor(nameof(IsCanDelected))]
    [ObservableProperty] private ScannerWrap? _scanner  ;
    [ObservableProperty] private BindingList<ScannerWrap> _scannerList = new();
    [ObservableProperty] private ScannerWrap _scanner1 = new ();
    [ObservableProperty] private string _message = string.Empty;

    public bool IsCanDelected => Scanner is not null && ScannerList.Contains(Scanner);

    public MainWindowViewModel()
    {
        LoadSetting();
    }
    [RelayCommand]
    void AddItem()
    {
        var sc = new ScannerWrap();
        sc.Scanner.UpdateMessageEvent += AddMessage;
        sc.Scanner.UpdateExceptionEvent += AddMessage;
        ScannerList.Add(sc);
    }

    [RelayCommand(CanExecute = nameof(IsCanDelected))]
    void RemoveItem()
    {
        if(Scanner is null || Scanner.Scanner is null) return;
        if (MessageBox.Show($"是否删除项[{Scanner.Scanner.Name}]","提示",MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (ScannerList.Contains(Scanner))
        {
            Scanner.Scanner.UpdateExceptionEvent -= AddMessage;
            Scanner.Scanner.UpdateMessageEvent -= AddMessage;
            ScannerList.Remove(Scanner);
        }
    }

    [RelayCommand]
    void SaveAllItems()
    {
        try
        {
            SaveSetting();
            MessageBox.Show(Application.Current.MainWindow, "保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow,$"保存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    void AddMessage(string msg)
    {
        Message += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
    }
    string settingFileName = "setting.json";

    JsonSerializerSettings settings= new JsonSerializerSettings() 
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
            var jsonString=JsonConvert.SerializeObject(ScannerList, settings);
            File.WriteAllText(settingFileName, jsonString);
            AddMessage("保存设置成功");
            return true;

        }
        catch (Exception ex)
        {
            AddMessage($"保存设置失败：{ex.Message}");
            return false;
        }
    }

    public bool LoadSetting()
    {
        try
        {
            if (!File.Exists(settingFileName))
            {
                AddMessage("设置文件不存在，加载默认设置");
                return false;
            }
            var jsonString = File.ReadAllText(settingFileName);
            var list = JsonConvert.DeserializeObject<BindingList<ScannerWrap>>(jsonString, settings);
            if (list is null)
            {
                AddMessage("设置文件内容无效，加载默认设置");
                return false;
            }
            ScannerList= list;
            foreach (var item in list)
            {
                item.Scanner.UpdateMessageEvent += AddMessage;
                item.Scanner.UpdateExceptionEvent += AddMessage;
                //ScannerList.Add(item);
            }
            AddMessage("加载设置成功");
            return true;
        }
        catch (Exception ex)
        {
            AddMessage($"加载设置失败：{ex.Message}");
            return false;
        }
    }

}