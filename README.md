# DiskSlim 硬盘瘦身

只读的磁盘空间分析工具：告诉你空间被谁占了、哪些可以放心清理、应该怎么清理。

**v0.1 只读取文件信息，不会修改或删除任何文件。**

## 快速开始

需要 .NET 10 运行时（Windows）。

```bash
dotnet run --project src/DiskSlim.Cli
```

或者先发布成单个 exe（约 370 KB）：

```bash
dotnet publish src/DiskSlim.Cli -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o dist
```

```bash
dist\diskslim.exe
```

以管理员身份运行可以统计到更多系统目录（比如 `C:\Windows\Temp`）。

## 常用命令

| 命令 | 作用 |
|---|---|
| `diskslim` | 完整报告：磁盘概况 + 可清理项目 + 空间分布 + 最大的文件 |
| `diskslim --rules-only` | 只检查清理规则，约 2 秒 |
| `diskslim --map-only` | 只看空间分布 |
| `diskslim -d D --top 30` | 分析 D 盘，每个列表显示 30 条 |
| `diskslim -v` | 显示每条规则命中的具体路径 |
| `diskslim --json r.json --md r.md` | 同时保存 JSON 和 Markdown 报告 |
| `diskslim --list-rules` | 列出所有规则 |

退出码：`0` 成功，`1` 出错，`2` 参数错误，`130` 被 Ctrl+C 取消。设置环境变量 `DISKSLIM_DEBUG=1` 可以看到完整的异常堆栈。

## 报告里的三个风险等级

| 等级 | 含义 | 例子 |
|---|---|---|
| 可放心清理 | 缓存和临时文件，删除后会自动重建 | 浏览器缓存、网易云缓存、npm/pip 缓存 |
| 需要确认 | 删除后要重新下载，或需要你判断 | 回收站、休眠文件、NuGet 包目录 |
| 仅供参考 | 用户数据或系统文件，不建议用工具清理 | 微信/QQ 聊天记录、下载文件夹、虚拟内存 |

## 扫描引擎的安全约定

- **不进入目录联接、符号链接和挂载点**：避免重复计算和死循环。
- **不读取 OneDrive 等云盘的“仅在线”文件**：它们不占本地空间，读取还会触发下载。
- **包含隐藏和系统文件**：比如 `pagefile.sys`、`hiberfil.sys`。
- **无权限的目录跳过并记录**，不会中断扫描；报告里会标出哪些数值可能偏小。
- **同一个路径只算一次**：规则按顺序评估，后面范围更宽的规则会扣掉前面规则已经统计过的部分。

## 规则文件

内置规则在 [src/DiskSlim.Core/Rules/default-rules.json](src/DiskSlim.Core/Rules/default-rules.json)，共 42 条，覆盖系统临时文件、浏览器、常用国内软件、开发工具缓存和用户文件夹。可以用 `--rules my-rules.json` 换成自己的规则。

```jsonc
{
  "version": 1,
  "rules": [
    {
      "id": "netease-cloudmusic-cache",      // 必填，唯一
      "name": "网易云音乐缓存",               // 必填
      "category": "应用",
      "paths": ["%LOCALAPPDATA%/NetEase/CloudMusic/Cache"],  // 必填，支持环境变量和 * ? 通配符
      "risk": "low",                          // 必填：low / medium / high
      "minAgeDays": 1,                        // 可选：只把 N 天前的文件计为可释放
      "processes": ["cloudmusic"],            // 可选：这些程序运行时提醒先关闭
      "advice": "在网易云音乐的设置里清除缓存……"
    }
  ]
}
```

- `paths` 里可以用 `/` 或 `\\` 分隔；通配符可以出现在任意一级，比如 `%LOCALAPPDATA%/Google/Chrome/User Data/*/Cache`。
- `::RecycleBin` 表示回收站。
- `risk` 必须明确填写：清理工具不能替规则作者假定风险等级。
- **具体的规则放前面，宽泛的规则放后面**（比如“下载文件夹里的安装包”要排在“下载文件夹”之前）。

## 项目结构

```
src/DiskSlim.Core/          扫描和规则引擎（以后的图形界面也复用这一层）
  Scanning/TreeScanner.cs   并行遍历目录树，统计大小
  Rules/                    规则模型、加载校验、路径展开、规则评估、默认规则
  Reporting/                报告模型和报告生成
  Platform/                 回收站查询、管理员权限、进程列表
src/DiskSlim.Cli/           命令行：参数解析、控制台输出、Markdown 导出
tests/DiskSlim.Tests/       xUnit 测试
```

运行测试：

```bash
dotnet test DiskSlim.slnx
```

## 已知限制

- 大小按文件长度统计，没有扣除 NTFS 压缩，也没有按簇计算实际占用。
- 硬链接会被重复计算，所以 `C:\Windows`（尤其是 WinSxS）的数值偏大。
- 没有管理员权限时，部分系统目录读不到，相关数值偏小。
- 系统还原点、NTFS 元数据等占用的空间无法通过遍历文件统计到，报告会给出“未统计到”的差额。

## 路线图

- **v0.2**：规则引擎加上“只模拟不删除”模式、真实清理和操作日志
- **v0.3**：图形界面（方块图、分类勾选、风险颜色）
- **v0.4**：重复文件查找、系统级清理（用管理员权限的助手进程调用 DISM 和磁盘清理）、隔离区和撤销
- **v0.5**：定时清理、空间增长追踪、扩充国内软件规则
