# 磁盘增量监控

定时扫描磁盘，和上一次的快照对比，生成一份 HTML 报告：哪些目录变大了，各涨了多少 MB。变化小于 10MB 的目录不展开，只往下钻有变化的分支，所以报告通常很短。

两个程序都是单文件，引擎脚本内嵌在 exe 里，拷到哪都能跑：

- DiskMonitorGUI.exe — 图形界面
- DiskMonCli.exe — 命令行（双击进交互模式）

## 文件说明

| 路径 | 作用 |
|---|---|
| src/GUI.cs | GUI 源码（C# 5，Windows 自带编译器能编） |
| src/Cli.cs | CLI 源码 |
| src/Common.cs | GUI 和 CLI 共用的逻辑 |
| src/app.manifest | GUI 清单：管理员 + DPI 感知 |
| src/compile.cmd | 构建脚本 |
| src/DiskMonitor.ps1 | 扫描引擎，构建时内嵌进 exe，计划任务以 SYSTEM 身份运行它 |
| src/Update-Task.ps1 | 计划任务注册脚本，构建时内嵌 |
| src/config.json | 默认配置模板，构建时内嵌；各机器的运行配置单独存放 |
| bin/ | 构建输出，不入库 |

## 构建

不装任何东西，Windows 自带的 csc.exe 就能编：

```bat
src\compile.cmd
```

改了 src 下的源码就重新跑一次，输出在 bin。

## 使用

1. 双击 DiskMonitorGUI.exe（弹一次 UAC），勾选要监控的盘，点"应用计划"
2. 之后每天自动出报告，凌晨没开机也没关系，开机会补跑；"立即扫描"手动触发，跑完自动用浏览器打开
3. 命令行：status、scan -open、schedule -days 2 -hour 9、unschedule、config、open

## 几个设计决定

- 扫描跑在 SYSTEM 的计划任务里。普通用户读不到 System Volume Information 这类目录，混着扫会让快照对比出现假的增减。
- 数据目录自动定位，代码里没有写死盘符：exe 旁的 DiskMonitorData → 注册表记忆 → ProgramData。
- 统计的是逻辑文件大小，跳过 junction 和符号链接。WinSxS 的硬链接会重复计数，但每天口径一样，不影响看增量。

---

本项目由 GLM 5.3（ZCode）完成。
