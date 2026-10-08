# 内存预留

Windows 托盘小工具。启动后锁住一块物理内存。剩余内存低于阈值时，按 256 MB 一段放开，编译继续跑，避免桌面被一次性挤死。系统报告内存不足时会提前开始放开。

## 用法

需要 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）。

```powershell
dotnet build src/MemReserve/MemReserve.csproj -c Release
src/MemReserve/bin/Release/net8.0-windows/MemReserve.exe
```

默认预留 2048 MB，剩余物理内存低于 2048 MB 时开始放开。关闭窗口只缩到托盘，右键「退出」才会放开内存并结束。

全部放开之后，剩余内存要连续 30 秒高于「阈值 + 预留 + 512 MB」才会重新占用。窗口里的「最近放开」留下最近四次到线放开时的进程名和当时剩余内存；名单里有 `msbuild`、`cl`、`node`、`rustc`、`java` 时优先记其中占用最高的一个。设置保存在 `%AppData%\MemReserve\settings.json`。

三个开关默认关闭：

- 开机时启动：登录后只进托盘。
- 编译硬上限：把 `msbuild`、`cl`、`node`、`rustc`、`java` 放进内存上限，或用「启动编译」跑一条命令。到顶后是编译分配失败。
- 到线时暂停编译：低于阈值时暂停上述进程里最占内存的一个，托盘里可选继续或结束。
