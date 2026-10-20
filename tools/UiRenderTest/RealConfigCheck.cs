using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using 模拟扫码枪;

namespace UiRenderTest;

/// <summary>
/// 用仓库中真实的 setting.json 验证：加载后内外层接口类型一致，且重新保存后能修正历史脏数据。
/// </summary>
internal static class RealConfigCheck
{
    public static int Run(string settingPath)
    {
        int fail = 0;
        Console.WriteLine($"\n[5] 真实配置文件 {Path.GetFileName(settingPath)}");

        if (string.IsNullOrWhiteSpace(settingPath) || !File.Exists(settingPath))
        {
            // 未显式指定路径时，自动向上查找仓库中的真实 setting.json。
            // 之前这里恒为 SKIP —— 调用方传的其实是「输出目录」，不是配置文件路径。
            settingPath = FindRealSettingFile();
            if (settingPath is null)
            {
                Console.WriteLine("  [SKIP] 未找到 setting.json");
                return 0;
            }
            Console.WriteLine($"  自动定位到 {settingPath}");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "扫码枪真实配置");
        Directory.CreateDirectory(workDir);
        var localFile = Path.Combine(workDir, "setting.json");
        File.Copy(settingPath, localFile, true);

        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        {
            // 显式注入隔离路径：配置文件位置已从「相对当前工作目录」改为固定路径，
            // 这里指定到临时目录，避免动到 exe 同级的真实配置。
            var vm = new MainWindowViewModel(localFile);
            Console.WriteLine($"  加载到 {vm.ScannerList.Count} 个模拟器");

            foreach (var w in vm.ScannerList)
            {
                var inner = w.Scanner.InterfaceType;
                var outer = w.InterfaceType;
                var typeName = w.Scanner.GetType().Name;
                var ok = inner == outer;
                Console.WriteLine($"  {(ok ? "[PASS]" : "[FAIL]")} {w.Scanner.Name}: 外层={outer} 内层={inner} 实例={typeName}");
                if (!ok) fail++;

                // 实例类型应与外层枚举对应
                bool typeOk = (outer, typeName) switch
                {
                    (EnumInterfaceType.SerialPort, "CommSerialPort") => true,
                    (EnumInterfaceType.TcpClient, "CommTcpClient") => true,
                    (EnumInterfaceType.TcpServer, "CommTcpServer") => true,
                    _ => false
                };
                if (!typeOk)
                {
                    Console.WriteLine($"         [FAIL] 实例类型与外层枚举不匹配");
                    fail++;
                }
            }

            // 重新保存，验证历史脏数据被修正
            vm.SaveSetting();
            var json = File.ReadAllText(localFile);
            var data = JsonConvert.DeserializeObject<System.Collections.Generic.List<Newtonsoft.Json.Linq.JObject>>(json);
            foreach (var item in data)
            {
                var outer = (EnumInterfaceType)item["InterfaceType"]!.ToObject<int>();
                var inner = (EnumInterfaceType)item["Scanner"]!["InterfaceType"]!.ToObject<int>();
                if (outer != inner)
                {
                    Console.WriteLine($"  [FAIL] 保存后仍有不一致: 外层={outer} 内层={inner}");
                    fail++;
                }
            }
            Console.WriteLine("  [PASS] 重新保存后内外层接口类型已一致");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [FAIL] 异常: " + ex.Message);
            fail++;
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
        }
        return fail;
    }

    /// <summary>
    /// 从程序集所在目录向上查找仓库根目录下的 setting.json。
    /// <para>
    /// 不能用当前工作目录：回归测试会把 CWD 切到临时目录，那里向上找不到仓库。
    /// </para>
    /// </summary>
    private static string? FindRealSettingFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var name in new[] { "setting.json" })
            {
                var p = Path.Combine(dir.FullName, name);
                if (File.Exists(p)) return p;
            }
            foreach (var sub in new[] { Path.Combine("模拟扫码枪", "bin", "Debug", "net6.0-windows") })
            {
                var p = Path.Combine(dir.FullName, sub, "setting.json");
                if (File.Exists(p)) return p;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
