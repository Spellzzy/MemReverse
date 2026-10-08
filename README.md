# 内存预留

Windows 托盘小工具。启动后锁住一块物理内存，编译把剩余内存压到阈值以下时整块放开，避免机器被吃满。

## 用法

需要 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）。

```powershell
dotnet build src/MemReserve/MemReserve.csproj -c Release
src/MemReserve/bin/Release/net8.0-windows/MemReserve.exe
```

默认预留 2048 MB，剩余物理内存低于 2048 MB 时释放。关闭窗口只缩到托盘，右键「退出」才会放开内存并结束。

释放之后，剩余内存要连续 30 秒高于「阈值 + 预留 + 512 MB」才会重新占用。设置保存在 `%AppData%\MemReserve\settings.json`。
