using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace 模拟扫码枪;

public partial class MainWindowViewModel:ObservableObject
{
    [ObservableProperty] private ScannerWrap? _scanner  ;
    [ObservableProperty] private BindingList<ScannerWrap> _scannerList = new();
    [ObservableProperty] private ScannerWrap _scanner1 = new ();
    [ObservableProperty] private string message = string.Empty;

    public MainWindowViewModel()
    {
        AddItem();
    }
    [RelayCommand]
    void AddItem()
    {
        var sc = new ScannerWrap();
        sc.Scanner.UpdateMessageEvent += AddMessage;
        sc.Scanner.UpdateExceptionEvent += AddMessage;
        ScannerList.Add(sc);
    }

    [RelayCommand]
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
    void AddMessage(string msg)
    {
        Message += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
    }

}