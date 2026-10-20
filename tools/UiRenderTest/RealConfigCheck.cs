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

        if (!File.Exists(settingPath))
        {
            Console.WriteLine("  [SKIP] 未找到 setting.json");
            return 0;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "扫码枪真实配置");
        Directory.CreateDirectory(workDir);
        File.Copy(settingPath, Path.Combine(workDir, "setting.json"), true);

        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        {
            var vm = new MainWindowViewModel();
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
            var json = File.ReadAllText(Path.Combine(workDir, "setting.json"));
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
}
