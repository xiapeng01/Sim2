# 单元测试项目说明

## 位置与运行

```bash
cd D:/project2/模拟扫码枪2/tests/模拟扫码枪.Tests

# 运行全部测试
dotnet test

# 只跑快速的纯逻辑测试（跳过需要真实端口/等待重试的慢用例）
dotnet test --filter "Category!=Slow"

# 只跑通信集成测试
dotnet test --filter "Category=Slow"

# 生成 HTML 报告
dotnet test --logger "html;LogFileName=report.html"
```

项目已加入 `模拟扫码枪.sln`，在 Visual Studio / Rider 中可直接运行与调试。

## 技术选型

| 项 | 选择 | 说明 |
|---|---|---|
| 框架 | xUnit 2.9.2 | 与 CommunityToolkit.Mvvm 社区习惯一致，Theory 数据驱动写参数化用例很顺手 |
| 目标框架 | net6.0-windows + UseWPF | 必须与被测项目一致，否则引用 WPF 类型会失败 |
| 运行器 | xunit.runner.visualstudio 2.8.2 | 支持 `dotnet test` |

## 目录结构

```
tests/模拟扫码枪.Tests/
├── 模拟扫码枪.Tests.csproj
├── TempDirTestBase.cs            隔离工作目录的测试基类 + 串行集合定义
├── ACommLogicTests.cs            AComm 基类纯逻辑（十六进制、字节比较、结束符、附加内容、属性通知、配置复制、Dispose）
├── ScannerWrapTests.cs           包装层（接口切换重建与参数保留、枚举中文描述）
├── ConverterTests.cs             五个 UI 转换器 + 取反转换器
├── ViewModelAndConfigTests.cs    配置持久化往返、脏数据修正、列表与命令行为
└── CommIntegrationTests.cs       真实回环端口的通信集成测试（标记 Slow）
```

## 当前规模：118 个用例全部通过

| 测试类 | 用例数 | 覆盖内容 |
|---|---:|---|
| `ACommLogicTests` | 21 | 十六进制解析、字节数组比较、结束符映射、附加内容生成、属性通知、配置复制、Dispose 幂等 |
| `ScannerWrapTests` | 14 | 接口切换重建底层实例、**切换后参数不丢失**、内外层类型一致性、枚举中文描述 |
| `ConverterTests` | 28 | 枚举可见性（含 `!` 取反）、布尔/空/整数可见性、枚举中文转换、取反转换器的 null 安全 |
| `ViewModelAndConfigTests` | 21 | 配置保存-加载往返、**历史脏数据自动修正**、损坏 JSON 容错、列表增删与选中、日志级别 |
| `CommIntegrationTests` | 13 | TCP 客户端重试与恢复、端口释放、发送内容送达、客户端连接数统计、端口冲突上报、串口失败容错 |

## 关键设计

### 1. 用 `TempDirTestBase` 隔离工作目录

`MainWindowViewModel` 用相对路径 `"setting.json"` 读写配置，依赖进程当前工作目录。
xUnit 默认并行执行不同测试类，多个测试同时 `Directory.SetCurrentDirectory` 会互相干扰，
表现为**单跑通过、全跑失败**。

解决方式：
- 所有依赖配置文件的测试类继承 `TempDirTestBase`，各自独占一个 GUID 命名的临时目录；
- 这些类统一标注 `[Collection(nameof(WorkingDirectoryCollection))]`，关闭并行；
- `Dispose` 由 xUnit 自动调用，负责恢复目录并清理。

> 顺带印证了报告 P1 #11：`setting.json` 使用相对路径本身就是缺陷，
> 建议后续改为 `AppContext.BaseDirectory`，届时本基类可简化为直接使用临时文件路径。

### 2. 用 `ConfirmRemove` 钩子绕开模态框

`RemoveItem()` 会弹 `MessageBox.Show`，在测试进程中会永久阻塞。
已在 `MainWindowViewModel` 中把确认逻辑抽成 `protected virtual bool ConfirmRemove(string name)`，
生产行为完全不变，测试则通过继承 `AutoConfirmViewModel` 自动确认，
并额外覆盖了"用户点取消则不删除"的分支。

### 3. 端口探测必须用 `IPAddress.Any`

`CommTcpServer` 绑定的是 `IPAddress.Any`（0.0.0.0）。
若测试用 `IPAddress.Loopback`（127.0.0.1）探测端口占用，**两者不冲突**，
会误判为"端口空闲"，导致"Dispose 后端口已释放"这类断言失去意义。
所有端口探测与抢占统一使用 `Any`。

### 4. P0 修复的回归防线

`CommIntegrationTests` 中以下用例直接对应上一轮修复的 P0 问题，
一旦回退会立即失败：

| 用例 | 对应的 P0 问题 |
|---|---|
| `TcpClient_对端未启动时应持续重试而不失效` | #2 连接失败杀死工作循环 |
| `TcpClient_端点就绪后应自动连接` | #2 失败后无法自愈 |
| `TcpServer_Dispose后监听端口应释放` | #4 端口不释放 |
| `TcpServer_端口被占用时应上报错误且不死循环崩溃` | #3 空 catch 吞异常 + 永不重试 |
| `串口_打开失败应上报错误而不抛出` | #1 串口打开失败杀死工作循环 |
| `三种通信类型_Dispose均应幂等不抛异常` | #4 Dispose 未做幂等保护 |

### 5. 已把缺陷固化为可观测行为

`ACommLogicTests.长度大于10时当前会抛越界异常` 用例记录了报告 P1 #6
（`AppendContentLength` 超过 10 时 `Substring` 越界）的**当前**行为。
修复该缺陷时，此用例应改为断言正常返回，届时即完成回归闭环。

## 注意事项

- 测试会真实占用本机 TCP 端口（均为临时随机端口，用完即释放），CI 环境需允许本地监听；
- `CommIntegrationTests` 标记了 `Category=Slow`，因需等待 2 秒退避重试周期，
  全量运行约 18 秒。如需在提交前快速自检，可用 `--filter "Category!=Slow"` 只跑 105 个快速用例；
- 串口用例只断言"不崩溃、错误被上报"，不依赖特定硬件存在。
