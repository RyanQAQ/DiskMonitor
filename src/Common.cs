// Common.cs — GUI 与 CLI 共用的类型和逻辑：数据目录定位、内嵌资源解压、计划任务操作
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DiskMonitorCommon
{
    public class Config
    {
        public List<string> Drives = new List<string>();
        public double ThresholdMB = 10;
        public int SnapshotMaxDepth = 6;
        public int ReportMaxDepth = 5;
        public int ReportKeepDays = 30;
        public int SnapshotKeepDays = 30;
        public int ScheduleDays = 1;
        public int ScheduleHour = 1;
        public int ScheduleMinute = 0;
    }

    public class RunState
    {
        public string Status = "";
        public string LastRunTime = "";
        public string ReportPath = "";
        public string Message = "";
    }

    public static class Common
    {
        public const string TaskName = "DiskDailyMonitor";
        public const string AppVersion = "0.1.0";   // 对外首个版本；小改 +0.0.1，大改 +0.1

        public static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;

        // 数据目录定位：exe 旁数据标记（DiskMonitorData/config/snapshots，数据跟着 exe 走）
        // -> 注册表记忆（同机挪 exe 不分裂数据）-> 全新则 exe 旁新建 DiskMonitorData（不可写退 ProgramData）
        public static string FindDataHome()
        {
            string dataDir = Path.Combine(ExeDir, "DiskMonitorData");
            if (Directory.Exists(dataDir)) return dataDir;
            if (File.Exists(Path.Combine(ExeDir, "config.json"))) return ExeDir;
            if (Directory.Exists(Path.Combine(ExeDir, "snapshots"))) return ExeDir;

            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\DiskMonitor"))
                {
                    if (k != null)
                    {
                        string pinned = k.GetValue("DataHome") as string;
                        if (!string.IsNullOrEmpty(pinned) && Directory.Exists(pinned)) return pinned;
                    }
                }
            }
            catch { }

            try
            {
                Directory.CreateDirectory(dataDir);
                return dataDir;
            }
            catch
            {
                try
                {
                    string pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskMonitor");
                    Directory.CreateDirectory(pd);
                    return pd;
                }
                catch { return null; }
            }
        }

        public static void PinHome(string dir)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\DiskMonitor"))
                    k.SetValue("DataHome", dir);
            }
            catch { }
        }

        // 把 exe 内嵌的引擎脚本同步到数据目录；config.json 已存在则不覆盖
        public static void SyncEmbedded(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
            try { WriteResource("engine.ps1", Path.Combine(dir, "DiskMonitor.ps1")); } catch { }
            try { WriteResource("updatetask.ps1", Path.Combine(dir, "Update-Task.ps1")); } catch { }
            string cfgPath = Path.Combine(dir, "config.json");
            if (!File.Exists(cfgPath)) { try { WriteResource("defaultconfig.json", cfgPath); } catch { } }
        }

        static void WriteResource(string name, string path)
        {
            using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new IOException("缺少内嵌资源: " + name);
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                    s.CopyTo(fs);
            }
        }

        // ---- 计划任务 ----

        public static bool TaskExists()
        {
            string r = RunCapturePS("(Get-ScheduledTask -TaskName '" + TaskName + "' -ErrorAction SilentlyContinue) -ne $null");
            return r == "True";
        }

        public static string QueryNextRun()
        {
            string r = RunCapturePS("$i = Get-ScheduledTaskInfo -TaskName '" + TaskName + "' -ErrorAction SilentlyContinue; if($i -and $i.NextRunTime){ $i.NextRunTime.ToString('yyyy-MM-dd HH:mm') }");
            return string.IsNullOrEmpty(r) ? "未知" : r;
        }

        static string RunCapturePS(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + command.Replace("\"", "\\\"") + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                string s = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(15000);
                return s;
            }
            catch { return ""; }
        }

        // 调 Update-Task.ps1 重注册计划任务，output 返回脚本输出或异常信息
        public static bool RunUpdateTask(string baseDir, int days, int hour, int minute, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(baseDir, "Update-Task.ps1") + "\" -DaysInterval " + days + " -Hour " + hour + " -Minute " + minute);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                output = (so + se).Trim();
                return p.ExitCode == 0;
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        // ---- 文件 ----

        public static RunState ReadState(string statePath)
        {
            try { if (File.Exists(statePath)) return new JavaScriptSerializer().Deserialize<RunState>(File.ReadAllText(statePath)); }
            catch { }
            return null;
        }

        // 允许其他进程同时写的读取（日志/状态文件在扫描期间被引擎持有）
        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return sr.ReadToEnd();
        }
    }
}
