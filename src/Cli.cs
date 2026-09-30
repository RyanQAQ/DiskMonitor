// DiskMonCli — 磁盘增量监控命令行（.NET Framework 4.x，C#5）
// 双击运行进交互模式，命令行参数用法见 PrintHelp。
// 除 help 外都要管理员权限，缺权限时会自动弹 UAC 重启自己，输出经临时文件传回。
// 共用逻辑见 Common.cs。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using DiskMonitorCommon;

namespace DiskMonCli
{
    public static class Program
    {
        static JavaScriptSerializer Json = new JavaScriptSerializer();
        static string BaseDir;
        static Config Cfg = new Config();

        static string ConfigPath { get { return Path.Combine(BaseDir, "config.json"); } }
        static string StatePath { get { return Path.Combine(BaseDir, "state.json"); } }
        static string LogPath { get { return Path.Combine(BaseDir, "last-run.log"); } }
        static string ReportDir { get { return Path.Combine(BaseDir, "reports"); } }

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var list = new List<string>(args);
            int oi = list.IndexOf("-__out");
            string outOverride = null;
            if (oi >= 0 && oi + 1 < list.Count) { outOverride = list[oi + 1]; list.RemoveRange(oi, 2); }
            if (outOverride != null)
            {
                var w = new StreamWriter(outOverride, false, Encoding.UTF8);
                w.AutoFlush = true;
                Console.SetOut(w);
            }
            args = list.ToArray();

            if (args.Length == 0)
            {
                // 双击运行：进入交互模式（可输入命令），而不是打印帮助后一闪而过
                ResolveBaseDir();
                PrintHelp();
                Console.WriteLine();
                Console.WriteLine("交互模式：输入命令回车执行，exit 退出。");
                return InteractiveLoop();
            }

            return Dispatch(args);
        }

        static int Dispatch(string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            if (verb == "help" || verb == "-h" || verb == "--help" || verb == "/?")
            {
                ResolveBaseDir();
                PrintHelp();
                return 0;
            }
            if (!IsElevated()) return RelaunchElevated(args);

            if (!ResolveBaseDir())
            {
                Console.WriteLine("错误: 无法确定数据目录。");
                Console.WriteLine("可设置环境变量 DISKMON_HOME 指定数据目录后重试。");
                return 3;
            }

            LoadConfig();
            switch (verb)
            {
                case "scan": return CmdScan(args);
                case "status": return CmdStatus();
                case "schedule": return CmdSchedule(args);
                case "unschedule": return CmdUnschedule();
                case "config": return CmdConfig(args);
                case "open": return CmdOpen();
                default:
                    Console.WriteLine("未知命令: " + verb);
                    PrintHelp();
                    return 1;
            }
        }

        // 交互模式命令循环（双击运行时进入）
        static int InteractiveLoop()
        {
            while (true)
            {
                Console.WriteLine();
                Console.Write("DiskMonCli> ");
                string line;
                try { line = Console.ReadLine(); } catch { return 0; }
                if (line == null) { Console.WriteLine(); return 0; }
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("q", StringComparison.OrdinalIgnoreCase)) return 0;
                if (line.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("clear", StringComparison.OrdinalIgnoreCase))
                { try { Console.Clear(); } catch { } continue; }
                try { Dispatch(line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)); }
                catch (Exception ex) { Console.WriteLine("出错: " + ex.Message); }
            }
        }

        // env DISKMON_HOME 强制指定；否则 Common.FindDataHome 自动定位，并同步内嵌引擎、记住位置
        static bool ResolveBaseDir()
        {
            BaseDir = null;
            string envHome = Environment.GetEnvironmentVariable("DISKMON_HOME");
            if (!string.IsNullOrEmpty(envHome))
            {
                try
                {
                    Directory.CreateDirectory(envHome);
                    BaseDir = envHome;
                    Common.SyncEmbedded(BaseDir);
                    return true;
                }
                catch { BaseDir = null; }
            }

            string home = Common.FindDataHome();
            if (home == null) return false;
            BaseDir = home;
            Common.SyncEmbedded(BaseDir);
            Common.PinHome(BaseDir);
            return true;
        }

        static bool IsElevated()
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        static int RelaunchElevated(string[] args)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "DiskMonCli-" + Guid.NewGuid().ToString("N") + ".out");
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = Assembly.GetExecutingAssembly().Location;
                psi.Arguments = JoinQuoted(args) + " -__out \"" + tmp + "\"";
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Console.WriteLine("需要管理员权限，请在弹出的 UAC 窗口中点\"是\"…");
                var p = Process.Start(psi);
                p.WaitForExit();
                string s = File.Exists(tmp) ? File.ReadAllText(tmp) : "(无输出)";
                Console.Write(s);
                return p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("UAC 被取消，命令未执行。请以管理员身份运行。");
                return 2;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        static string JoinQuoted(string[] args)
        {
            var parts = new List<string>();
            foreach (var a in args)
            {
                if (a.Contains(" ")) parts.Add("\"" + a + "\"");
                else parts.Add(a);
            }
            return string.Join(" ", parts.ToArray());
        }

        static void LoadConfig()
        {
            Cfg = new Config();
            try
            {
                if (File.Exists(ConfigPath))
                    Cfg = Json.Deserialize<Config>(File.ReadAllText(ConfigPath));
                if (Cfg.Drives == null) Cfg.Drives = new List<string>();
            }
            catch { Cfg = new Config(); }
        }

        static void SaveConfig()
        {
            File.WriteAllText(ConfigPath, Json.Serialize(Cfg), Encoding.UTF8);
        }

        static string Opt(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        static bool HasFlag(string[] args, string name)
        {
            return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        static List<string> ParseDrives(string s)
        {
            var result = new List<string>();
            foreach (var raw in s.Split(new[] { ',', '，', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string L = raw.Trim().TrimEnd(':').ToUpperInvariant();
                if (L.Length == 1 && L[0] >= 'A' && L[0] <= 'Z' && Directory.Exists(L + ":\\")) result.Add(L);
                else Console.WriteLine("跳过无效磁盘: " + raw);
            }
            return result;
        }

        static void EnsureTask()
        {
            if (!Common.TaskExists())
            {
                Console.WriteLine("计划任务未注册，正在按当前配置注册…");
                string o;
                Common.RunUpdateTask(BaseDir, Cfg.ScheduleDays, Cfg.ScheduleHour, Cfg.ScheduleMinute, out o);
            }
        }

        // ---------- 命令 ----------

        static int CmdScan(string[] args)
        {
            string drives = Opt(args, "-drives");
            string th = Opt(args, "-threshold");
            if (drives != null) Cfg.Drives = ParseDrives(drives);
            if (th != null) { double v; if (double.TryParse(th, out v)) Cfg.ThresholdMB = v; }
            SaveConfig();
            if (Cfg.Drives.Count == 0) { Console.WriteLine("没有可监控的磁盘。"); return 1; }
            Console.WriteLine("监控磁盘: {0}   阈值: {1} MB", string.Join(",", Cfg.Drives.ToArray()), Cfg.ThresholdMB);
            EnsureTask();

            long len0 = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
            var psi = new ProcessStartInfo("schtasks.exe", "/run /tn " + Common.TaskName);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            var p = Process.Start(psi);
            string msg = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit();
            if (p.ExitCode != 0) { Console.WriteLine("启动扫描失败: " + msg); return 1; }

            Console.WriteLine("已触发扫描，等待完成（实时日志如下）…");
            string prev = "";
            bool reset = false;
            var t0 = DateTime.Now;
            while (true)
            {
                Thread.Sleep(800);
                string txt = "";
                try { txt = File.Exists(LogPath) ? Common.ReadShared(LogPath) : ""; } catch { }
                if (txt.Length < len0) reset = true;
                if (reset)
                {
                    if (txt.StartsWith(prev) && txt.Length >= prev.Length)
                    {
                        if (txt.Length > prev.Length) Console.Write(txt.Substring(prev.Length));
                    }
                    else Console.Write(txt);
                    prev = txt;
                }
                if (File.Exists(StatePath)) break;
                if ((DateTime.Now - t0).TotalMinutes > 20) { Console.WriteLine("\n等待超时（可能已有扫描实例在运行）。"); break; }
            }

            RunState st = Common.ReadState(StatePath);
            if (st != null)
            {
                Console.WriteLine("状态: " + st.Status + (string.IsNullOrEmpty(st.Message) ? "" : "  (" + st.Message + ")"));
                if (st.Status == "ok" && File.Exists(st.ReportPath))
                {
                    Console.WriteLine("报告: " + st.ReportPath);
                    if (HasFlag(args, "-open"))
                    {
                        try { Process.Start(st.ReportPath); } catch { }
                    }
                }
                return st.Status == "ok" ? 0 : 1;
            }
            return 1;
        }

        static int CmdStatus()
        {
            Console.WriteLine("磁盘增量监控 v" + Common.AppVersion + " — 状态");
            Console.WriteLine("数据目录:   " + BaseDir);
            Console.WriteLine("监控磁盘:   " + (Cfg.Drives.Count > 0 ? string.Join(",", Cfg.Drives.ToArray()) : "(未设置)"));
            Console.WriteLine("增量阈值:   {0} MB", Cfg.ThresholdMB);
            Console.WriteLine("保留期:     报告 {0} 天 / 快照 {1} 天", Cfg.ReportKeepDays, Cfg.SnapshotKeepDays);
            Console.WriteLine("计划:       每 {0} 天 {1:00}:{2:00}（任务: {3}）", Cfg.ScheduleDays, Cfg.ScheduleHour, Cfg.ScheduleMinute, Common.TaskExists() ? "已注册" : "未注册");
            Console.WriteLine("下次运行:   " + (Common.TaskExists() ? Common.QueryNextRun() : "—"));
            RunState st = Common.ReadState(StatePath);
            if (st != null)
            {
                Console.WriteLine("上次运行:   {0}  状态: {1}{2}", st.LastRunTime, st.Status, string.IsNullOrEmpty(st.Message) ? "" : "  (" + st.Message + ")");
                if (st.Status == "ok" && File.Exists(st.ReportPath)) Console.WriteLine("最近报告:   " + st.ReportPath);
            }
            else Console.WriteLine("上次运行:   （尚无记录）");
            return 0;
        }

        static int CmdSchedule(string[] args)
        {
            int days = IntOrDefault(Opt(args, "-days"), Cfg.ScheduleDays);
            int hour = IntOrDefault(Opt(args, "-hour"), Cfg.ScheduleHour);
            int minute = IntOrDefault(Opt(args, "-minute"), Cfg.ScheduleMinute);
            if (days < 1 || days > 365) { Console.WriteLine("-days 须在 1~365 之间"); return 1; }
            if (hour < 0 || hour > 23) { Console.WriteLine("-hour 须在 0~23 之间"); return 1; }
            if (minute < 0 || minute > 59) { Console.WriteLine("-minute 须在 0~59 之间"); return 1; }
            Cfg.ScheduleDays = days; Cfg.ScheduleHour = hour; Cfg.ScheduleMinute = minute;
            SaveConfig();
            string o;
            if (!Common.RunUpdateTask(BaseDir, days, hour, minute, out o))
            {
                Console.WriteLine("计划更新失败。 " + o);
                return 1;
            }
            Console.WriteLine("计划已更新: 每 {0} 天 {1:00}:{2:00} 运行一次", days, hour, minute);
            Console.WriteLine("下次运行:   " + Common.QueryNextRun());
            return 0;
        }

        // 删除计划任务。不做交互确认：UAC 提权重启后没有交互终端可提问。
        // 误删了也无妨，一条 schedule 命令就能重新注册。
        static int CmdUnschedule()
        {
            if (!Common.TaskExists()) { Console.WriteLine("计划任务未注册，无需删除。"); return 0; }
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/delete /tn " + Common.TaskName + " /f");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Console.Write((so + se).Trim());
                Console.WriteLine();
                if (p.ExitCode == 0)
                {
                    Console.WriteLine("计划任务已删除，定时扫描停止。");
                    Console.WriteLine("快照与报告数据全部保留；重新注册用 schedule 命令（如 schedule -days 1 -hour 1）。");
                    return 0;
                }
                return 1;
            }
            catch (Exception ex) { Console.WriteLine("删除失败: " + ex.Message); return 1; }
        }

        static int CmdConfig(string[] args)
        {
            string drives = Opt(args, "-drives");
            string th = Opt(args, "-threshold");
            string rk = Opt(args, "-report-keep");
            string sk = Opt(args, "-snap-keep");
            if (drives != null) Cfg.Drives = ParseDrives(drives);
            if (th != null) { double v; if (double.TryParse(th, out v)) Cfg.ThresholdMB = v; }
            if (rk != null) { int v; if (int.TryParse(rk, out v) && v >= 1) Cfg.ReportKeepDays = v; }
            if (sk != null) { int v; if (int.TryParse(sk, out v) && v >= 1) Cfg.SnapshotKeepDays = v; }
            SaveConfig();
            Console.WriteLine("配置已保存:");
            Console.WriteLine("  监控磁盘:   {0}", string.Join(",", Cfg.Drives.ToArray()));
            Console.WriteLine("  增量阈值:   {0} MB", Cfg.ThresholdMB);
            Console.WriteLine("  报告保留:   {0} 天", Cfg.ReportKeepDays);
            Console.WriteLine("  快照保留:   {0} 天", Cfg.SnapshotKeepDays);
            Console.WriteLine("  计划:       每 {0} 天 {1:00}:{2:00}（用 schedule 命令修改）", Cfg.ScheduleDays, Cfg.ScheduleHour, Cfg.ScheduleMinute);
            return 0;
        }

        static int CmdOpen()
        {
            if (!Directory.Exists(ReportDir)) { Console.WriteLine("报告文件夹不存在: " + ReportDir); return 1; }
            var rep = Directory.GetFiles(ReportDir, "report-*.html").OrderByDescending(f => f).FirstOrDefault();
            if (rep == null) { Console.WriteLine("还没有报告文件。"); return 1; }
            try { Process.Start(rep); Console.WriteLine("已打开: " + rep); return 0; }
            catch (Exception ex) { Console.WriteLine("打开失败: " + ex.Message); return 1; }
        }

        static int IntOrDefault(string s, int def)
        {
            int v;
            return (s != null && int.TryParse(s, out v)) ? v : def;
        }

        static void PrintHelp()
        {
            Console.WriteLine("DiskMonCli v" + Common.AppVersion + " — 磁盘增量监控");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  status                                       状态一览");
            Console.WriteLine("  scan [-drives C,E] [-threshold MB] [-open]   立即扫描，-open 打开报告");
            Console.WriteLine("  schedule -days N -hour H [-minute M]         修改计划");
            Console.WriteLine("  unschedule                                   删除计划任务（数据保留）");
            Console.WriteLine("  config [-drives C,E] [-threshold MB] [-report-keep 天] [-snap-keep 天]");
            Console.WriteLine("  open                                         打开最新报告");
            Console.WriteLine("  help                                         帮助");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  schedule -days 2 -hour 9");
            Console.WriteLine("  scan -drives C,E -open");
            Console.WriteLine();
            Console.WriteLine("数据目录: " + (BaseDir ?? "自动选择（exe 旁 DiskMonitorData）"));
        }
    }
}
