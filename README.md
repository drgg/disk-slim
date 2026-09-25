# DiskSlim 硬盘瘦身

磁盘空间分析和清理工具：告诉你空间被谁占了、哪些可以放心清理，并且安全地帮你清理。

- **图形界面** `DiskSlimApp.exe`：勾选要清理的项目，用方块图浏览空间分布。
- **命令行** `diskslim.exe`：`diskslim`（scan）**只读**；`diskslim clean` **默认只模拟**，加 `--execute` 并确认后才真正清理。

两者共用同一套扫描、规则和清理引擎，每次清理都记录在操作日志里。

## 快速开始

需要 .NET 10 桌面运行时（Windows）。

```bash
dotnet run --project src/DiskSlim.Desktop
```

```bash
dotnet run --project src/DiskSlim.Cli
```

或者发布成单个 exe（各约 450 KB）：

```bash
dotnet publish src/DiskSlim.Desktop -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o dist
```

```bash
dotnet publish src/DiskSlim.Cli -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o dist
```

以管理员身份运行可以统计和清理更多系统目录（比如 `C:\Windows\Temp`）。图形界面里有“以管理员身份运行”按钮。

## 图形界面

打开后自动扫描系统盘（约 5～10 秒，只读取、不修改），然后有三个页面：

- **可清理项目**：按“可放心清理 / 需要确认 / 仅供参考”分组。“可放心清理”默认勾选，“需要确认”要手动勾选，“仅供参考”不能勾选。相关程序正在运行的项目会标出来，底部的“预计释放”不会把它们算进去。
- **空间分布**：方块图，面积代表大小；点击方块进入该文件夹，上方路径可以返回。
  - 绿 / 黄 / 红色方块是规则命中的目录（可放心清理 / 需要确认 / 仅供参考），蓝紫灰色是普通文件夹；
  - 方块底部的绿色细条表示里面有多少可以清理，方便顺着它一层层找到可清理的内容；
  - 右侧列出当前目录的子项和各自的可清理空间；右键可以在资源管理器中显示。
- **最大的文件**：最大的 50 个文件。
- **重复文件**：选择位置和最小文件大小后查找，勾选的副本移入隔离区（见下文）。
- **隔离区**：查看、恢复或彻底删除之前移入隔离区的内容。

点“清理选中项…”后先弹出**模拟结果**：每一项会怎么处理、多少文件、为什么跳过。点“开始清理”才执行，可以随时“停止”。完成后显示磁盘剩余空间的变化和日志位置，并自动重新扫描。

### 截图模式

用于检查界面或生成文档配图：在屏幕外打开窗口，扫描后渲染成 PNG 并退出。清理对话框只做模拟，**截图模式永远不会执行清理**。

```bash
DiskSlimApp.exe --screenshot main.png --tab 1 --navigate "C:\Users" --cleanup-dialog dialog.png
```

`--tab`：0 可清理项目，1 空间分布，2 最大的文件，3 重复文件（会自动查找一次，只读），4 隔离区；`--size 1280x820` 设置窗口大小。

## 命令行：查看报告 `diskslim scan`

| 命令 | 作用 |
|---|---|
| `diskslim` | 完整报告：磁盘概况 + 可清理项目 + 空间分布 + 最大的文件 |
| `diskslim --rules-only` | 只检查清理规则，约 2 秒 |
| `diskslim --map-only` | 只看空间分布 |
| `diskslim -d D --top 30` | 分析 D 盘，每个列表显示 30 条 |
| `diskslim -v` | 显示每条规则命中的具体路径 |
| `diskslim --json r.json --md r.md` | 同时保存 JSON 和 Markdown 报告 |
| `diskslim --list-rules` | 列出所有规则和它们的清理方式 |

报告里能自动清理的项目会提示对应的 `diskslim clean` 命令。

## 命令行：清理 `diskslim clean`

```bash
diskslim clean
```

```bash
diskslim clean --execute
```

```bash
diskslim clean --rule recycle-bin,nuget-packages --execute
```

1. **先模拟**：列出每一项会怎么处理、多少文件、多大，以及哪些会被跳过、为什么。
2. **再确认**：加 `--execute` 后，还要在提示里输入 `y` 才会执行（脚本里可以用 `--yes` 跳过）。
3. **执行并记录**：每个删除的文件都写进操作日志，结束时用磁盘剩余空间的前后差值报告实际释放了多少。

### 选哪些项目

| 风险等级 | 默认 | 说明 |
|---|---|---|
| 可放心清理 | 自动选中 | 缓存和临时文件，删除后会自动重建 |
| 需要确认 | 必须用 `--rule <id>` 点名 | 删除后要重新下载，或需要你判断（回收站、NuGet 包目录……）；默认移入隔离区，可以撤销 |
| 仅供参考 | **工具拒绝清理**，点名也不行 | 聊天记录、下载文件夹、系统文件 |

用 `--rule` 点名后，只清理点名的项目。

### 怎么清理

| 方式 | 用于 | 说明 |
|---|---|---|
| 永久删除 | “可放心清理”的缓存、临时文件 | 删除目录里的文件，**目录本身保留**；清空了的子目录一并删除 |
| 移入隔离区 | “需要确认”的内容（NuGet 包目录、模型缓存……） | 瞬间移走，**随时可以恢复**；确认没问题后再彻底删除才释放空间 |
| 移到回收站 | 用户文件（如下载的安装包） | 可以找回；如果文件放不进回收站，会弹窗确认，绝不悄悄永久删除 |
| 清空回收站 | 回收站 | |
| 彻底删除 | 隔离区中超过 7 天的内容 | 作为“可放心清理”的项目出现在 `diskslim clean` 里 |

### 隔离区

“需要确认”的项目不会被直接永久删除，而是先移入隔离区：

- 位置和原文件在同一块盘上（系统盘是 `%LOCALAPPDATA%\DiskSlim\Quarantine`，其他盘是 `盘符:\DiskSlim.Quarantine`），所以移入只是改名，瞬间完成。
- 每次清理是一批，清单记录每个文件原来的位置。清单是先写后移，程序中途崩溃也不会丢失原始位置。
- **恢复永远不覆盖**：原位置已经有同名文件时跳过并报告。
- 隔离区里的文件还占着空间，彻底删除后才释放。超过 7 天的批次会出现在下一次 `diskslim clean` 的“可放心清理”里。

```bash
diskslim quarantine
```

```bash
diskslim quarantine restore 20260925-105141-043b
```

```bash
diskslim quarantine purge --expired --execute
```

`purge` 也可以指定一个批次或用 `--all`；和 `clean` 一样默认只模拟，加 `--execute` 并确认后才删除。图形界面里有“隔离区”页面，清理完成的对话框里也可以一键撤销。

### 安全设计

每个文件都要同时满足下面所有条件才会被处理：

- **不属于其他规则**：比如清理 AppData 里的某个缓存时，属于“聊天记录”规则的目录会被整体跳过，即使那条规则没有被选中。
- **不是链接**：目录联接、符号链接既不进入也不删除；云盘“仅在线”文件不碰。
- **没有只读或系统属性**。
- **足够旧**：规则设了 `minAgeDays` 时，按“修改时间和创建时间中较晚的那个”判断。刚解压出来的文件保留着压缩包里的旧修改时间，但创建时间是刚才，可能正被安装程序使用，不会被当成旧文件。空目录同理。

在此之上还有几道防线：

- **关键目录保护**：磁盘根目录、用户目录、文档、桌面、下载、AppData 根目录、Windows 目录不能作为清理目标；System32、WinSxS、Program Files、凭据和证书目录里的任何东西都不能清理。自定义规则也绕不过去。
- **清理目标本身是链接时拒绝执行**，防止一个无害路径指向重要数据。
- **相关程序在运行时跳过整条规则**（例如网易云音乐开着时不清理它的缓存）。
- **正在使用的文件自动跳过**，并在结果里列出。
- **没有日志就不执行**：日志文件创建失败时直接退出。
- **Ctrl+C 安全取消**：处理完当前文件后停下，已完成的部分都在日志里。

模拟和执行走的是同一段代码，只是模拟时不调用任何写操作，所以模拟结果就是真实执行会做的事。扫描报告里的“可释放”也按同样的条件计算，三者的数字一致。

### 操作日志

默认保存在 `%LOCALAPPDATA%\DiskSlim\logs\clean-日期-时间.jsonl`，可以用 `--log` 指定。每行一条 JSON 记录：

```json
{"time":"2026-09-24T18:33:40+08:00","event":"deleted","rule":"npm-cache","path":"C:\\Users\\...\\index-v5\\00\\a1","bytes":16384}
```

`event` 的取值：`run-start`、`rule-start`、`deleted`、`recycled`、`quarantined`、`removed-dir`、`emptied-recycle-bin`、`purged-quarantine`、`failed`、`rule-end`、`run-end`。

## 重复文件：`diskslim dupes`

```bash
diskslim dupes
```

```bash
diskslim dupes D:\照片 E:\备份盘 --min-size 10MB
```

```bash
diskslim dupes --remove --execute
```

- **只读查找**，按成本从低到高缩小范围：先按大小分组，再比较文件头尾各 16 KB 的哈希，最后才计算完整的 SHA-256。用户目录一般几秒就能查完。
- **硬链接不算重复**：同一个物理文件的多个路径删掉也不省空间（uv、pnpm 等大量使用硬链接）。
- **默认不查**：`AppData`、`ProgramData`（软件自带同一个 DLL 很正常）、以 `.` 开头的工具目录和 `node_modules`、安全防线里的禁区。
- **受保护、只列出不能移除**：聊天记录等“仅供参考”的软件数据（被软件内部引用）、路径带 backup / 备份 的文件、DLL 等程序库。组里有受保护的文件时整组不给建议。
- **只建议移除明显的副本**：文件名带“(1)”“副本”“- Copy”，或在下载文件夹里。其他重复文件（例如项目里被引用的资源）只列出，由你自己判断——图形界面里可以手动勾选。
- **移除 = 移入隔离区**，可以恢复。每组至少保留一份；保留的那份在扫描后被改过或删除时整组不动；要移除的文件在扫描后被改过也不动。

## 退出码

`0` 成功（包括在确认提示里选择了取消），`1` 出错，`2` 参数错误或选择了不能清理的项目，`130` 被 Ctrl+C 取消。设置环境变量 `DISKSLIM_DEBUG=1` 可以看到完整的异常堆栈。

## 规则文件

内置规则在 [src/DiskSlim.Core/Rules/default-rules.json](src/DiskSlim.Core/Rules/default-rules.json)，共 43 条，其中 27 条可以自动清理。可以用 `--rules my-rules.json` 换成自己的规则。

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
      "action": "delete",                     // 可选：none（默认）/ delete / quarantine / recycle / empty-recycle-bin / purge-quarantine
      "minAgeDays": 1,                        // 可选：只处理 N 天前的文件
      "processes": ["cloudmusic"],            // 可选：这些程序运行时跳过
      "advice": "在网易云音乐的设置里清除缓存……"
    }
  ]
}
```

- `risk` 必须明确填写；`action` 不写就是 `none`，工具只给建议。**清理工具不替规则作者做安全假设。**
- `risk` 为 `high` 的规则不能设置 `action`；回收站（`::RecycleBin`）只能用 `empty-recycle-bin`。
- `paths` 里可以用 `/` 或 `\\` 分隔；通配符可以出现在任意一级。
- **具体的规则放前面，宽泛的规则放后面**：同一个路径只归属第一条命中它的规则。

## 项目结构

```
src/DiskSlim.Core/            扫描、规则和清理引擎（命令行和图形界面共用）
  Scanning/TreeScanner.cs     并行遍历目录树，统计大小
  Rules/                      规则模型、加载校验、路径展开和归属（RuleMatcher）、规则评估、默认规则
  Cleaning/                   清理计划、安全防线、执行器、文件操作、操作日志
  Reporting/                  报告模型和报告生成
  Quarantine/                 隔离区：移入、恢复、彻底删除
  Duplicates/                 重复文件查找和移除
  Visualization/              方块图布局（Squarified Treemap）、可清理空间索引
  Presentation/               两个界面共用的显示文字
  Platform/                   回收站、管理员权限、进程列表
src/DiskSlim.Cli/             命令行：参数解析、scan 和 clean 命令、控制台输出、Markdown 导出
src/DiskSlim.Desktop/         图形界面（WPF）：视图模型、方块图控件、清理对话框、截图模式
tests/DiskSlim.Tests/         xUnit 测试（含视图模型）
```

写操作集中在 [Cleaning/FileOperations.cs](src/DiskSlim.Core/Cleaning/FileOperations.cs)（删除、回收站）和 [Quarantine/QuarantineStore.cs](src/DiskSlim.Core/Quarantine/QuarantineStore.cs)（隔离区的移入、恢复、彻底删除），便于审查。

运行测试：

```bash
dotnet test DiskSlim.slnx
```

## 已知限制

- 大小按文件长度统计，没有扣除 NTFS 压缩，也没有按簇计算实际占用。
- 硬链接会被重复计算，所以 `C:\Windows`（尤其是 WinSxS）的数值偏大；uv、pnpm 等用硬链接的缓存，实际释放的空间可能比显示的少。
- 没有管理员权限时，部分系统目录读不到，相关数值偏小，也清理不了。
- 模拟运行无法预知哪些文件正被占用，这部分要到真正执行时才知道。
- Windows 更新缓存、缩略图缓存、Windows.old 等需要系统工具处理，目前只给建议。

## 路线图

- ~~**v0.1**：只读扫描和报告~~
- ~~**v0.2**：模拟运行、真实清理、安全防线、操作日志~~
- ~~**v0.3**：图形界面（方块图、分类勾选、风险颜色）~~
- **v0.4**：重复文件查找、系统级清理（用管理员权限的助手进程调用 DISM 和磁盘清理）、隔离区和撤销
- **v0.5**：定时清理、空间增长追踪、扩充国内软件规则
