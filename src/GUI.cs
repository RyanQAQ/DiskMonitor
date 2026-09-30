// DiskMonitorGUI — 磁盘增量监控（.NET Framework 4.x WinForms，C#5）
// 管理员运行（requireAdministrator 清单）：要启动 SYSTEM 扫描任务、改计划、查下次运行时间。
// 引擎脚本内嵌在 exe 里，启动时同步到数据目录。共用逻辑见 Common.cs。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using DiskMonitorCommon;

namespace DiskMonitorGui
{
    public class MainForm : Form
    {
        string baseDir;
        string ConfigPath { get { return Path.Combine(baseDir, "config.json"); } }
        string StatePath { get { return Path.Combine(baseDir, "state.json"); } }
        string LogPath { get { return Path.Combine(baseDir, "last-run.log"); } }
        string ReportDir { get { return Path.Combine(baseDir, "reports"); } }

        JavaScriptSerializer json = new JavaScriptSerializer();
        Config cfg = new Config();

        CheckedListBox clbDrives;
        NumericUpDown numThreshold, numReportKeep, numSnapKeep, numDays, numHour, numMinute;
        Button btnToggleAll, btnSave, btnApply, btnRemoveTask, btnScan, btnOpenReports;
        CheckBox chkOpen;
        Label lblTask, lblNextRun, lblStatus, lblLastRun;
        TextBox txtLog;
        Timer timer;

        bool scanWaiting = false;
        DateTime scanStart;
        long lastLogLen = -1;

        public MainForm()
        {
            bool ok = ResolveBaseDir();
            BuildUi();

            if (!ok)
            {
                MessageBox.Show("无法找到或创建数据目录。\r\n可设置环境变量 DISKMON_HOME 指定数据目录后重试。",
                    "磁盘增量监控", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                btnScan.Enabled = false; btnApply.Enabled = false;
                AppendLog("[GUI] 数据目录不可用，功能受限。");
                return;
            }

            AppendLog("[GUI] 数据目录: " + baseDir + (baseDir == Common.ExeDir ? "（文件夹模式）" : "（引擎已从 exe 内嵌资源同步）"));
            LoadConfigToUi();
            LoadDrives();
            RefreshTaskInfo();
            ShowLastState();
            TailLog(true);
        }

        // env DISKMON_HOME 强制指定；否则 Common.FindDataHome 自动定位，并同步内嵌引擎、记住位置
        bool ResolveBaseDir()
        {
            baseDir = null;
            string envHome = Environment.GetEnvironmentVariable("DISKMON_HOME");
            if (!string.IsNullOrEmpty(envHome))
            {
                try
                {
                    Directory.CreateDirectory(envHome);
                    baseDir = envHome;
                    Common.SyncEmbedded(baseDir);
                    return true;
                }
                catch { baseDir = null; }
            }

            string home = Common.FindDataHome();
            if (home == null) return false;
            baseDir = home;
            Common.SyncEmbedded(baseDir);
            Common.PinHome(baseDir);
            return true;
        }

        void BuildUi()
        {
            // 布局按 96 DPI 设计，启动时按实际 DPI 等比缩放坐标（清单已声明 DPI 感知，文字本身清晰）。
            // 不用 AutoScaleMode.Dpi：框架对手动添加的控件缩放不可靠，字体放大而标签框不放大，文字会被截断。
            AutoScaleMode = AutoScaleMode.None;
            float dpi = DeviceDpi;
            Func<int, int> S = delegate(int v) { return (int)Math.Round(v * dpi / 96f); };

            Text = "磁盘增量监控_v" + Common.AppVersion;
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(S(820), S(700));
            MinimumSize = new Size(S(640), S(520));
            StartPosition = FormStartPosition.CenterScreen;
            Shown += delegate
            {
                Rectangle wa = Screen.GetWorkingArea(this);
                if (Height > wa.Height) Height = wa.Height;
                if (Width > wa.Width) Width = wa.Width;
            };

            // ---- 监控设置 ----
            var grpMon = new GroupBox();
            grpMon.Text = "监控设置";
            grpMon.SetBounds(S(12), S(12), S(796), S(178));
            grpMon.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var lbl1 = new Label(); lbl1.Text = "监控磁盘:"; lbl1.AutoSize = true; lbl1.SetBounds(S(15), S(26), S(300), S(18));
            clbDrives = new CheckedListBox(); clbDrives.SetBounds(S(15), S(48), S(300), S(98));
            clbDrives.CheckOnClick = true;

            // 右侧参数表单：标签与输入框同高同 Top，文字右对齐
            var lblTh = new Label(); lblTh.Text = "增量阈值 (MB):"; lblTh.TextAlign = ContentAlignment.MiddleRight;
            lblTh.SetBounds(S(355), S(48), S(115), S(24));
            numThreshold = new NumericUpDown(); numThreshold.SetBounds(S(478), S(48), S(90), S(24));
            numThreshold.Minimum = 1; numThreshold.Maximum = 1000000; numThreshold.ThousandsSeparator = true;

            var lblRk = new Label(); lblRk.Text = "报告保留 (天):"; lblRk.TextAlign = ContentAlignment.MiddleRight;
            lblRk.SetBounds(S(355), S(78), S(115), S(24));
            numReportKeep = new NumericUpDown(); numReportKeep.SetBounds(S(478), S(78), S(90), S(24));
            numReportKeep.Minimum = 1; numReportKeep.Maximum = 3650;

            var lblSk = new Label(); lblSk.Text = "快照保留 (天):"; lblSk.TextAlign = ContentAlignment.MiddleRight;
            lblSk.SetBounds(S(355), S(108), S(115), S(24));
            numSnapKeep = new NumericUpDown(); numSnapKeep.SetBounds(S(478), S(108), S(90), S(24));
            numSnapKeep.Minimum = 1; numSnapKeep.Maximum = 3650;

            btnToggleAll = new Button(); btnToggleAll.Text = "全选 / 全不"; btnToggleAll.SetBounds(S(15), S(142), S(100), S(28));
            btnToggleAll.Click += delegate { ToggleAllDrives(); };

            btnSave = new Button(); btnSave.Text = "保存设置"; btnSave.SetBounds(S(478), S(142), S(90), S(28));
            btnSave.Click += delegate { SaveConfig(); };

            grpMon.Controls.Add(lbl1); grpMon.Controls.Add(clbDrives);
            grpMon.Controls.Add(lblTh); grpMon.Controls.Add(numThreshold);
            grpMon.Controls.Add(lblRk); grpMon.Controls.Add(numReportKeep);
            grpMon.Controls.Add(lblSk); grpMon.Controls.Add(numSnapKeep);
            grpMon.Controls.Add(btnToggleAll); grpMon.Controls.Add(btnSave);

            // ---- 计划任务 ----
            var grpSch = new GroupBox();
            grpSch.Text = "计划任务";
            grpSch.SetBounds(S(12), S(202), S(796), S(86));
            grpSch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // 行内标签不用 AutoSize（实际高度随字体/DPI 漂移，和数字框对不齐），固定高度 24、
            // 与数字框同 Top、文字垂直居中；按钮高 30，Top 比数字框少 3，中心线一致。
            var lblD = new Label(); lblD.Text = "每"; lblD.TextAlign = ContentAlignment.MiddleLeft;
            lblD.SetBounds(S(15), S(29), S(24), S(24));
            numDays = new NumericUpDown(); numDays.SetBounds(S(40), S(29), S(56), S(24));
            numDays.Minimum = 1; numDays.Maximum = 365;
            var lblD2 = new Label(); lblD2.Text = "天运行一次"; lblD2.TextAlign = ContentAlignment.MiddleLeft;
            lblD2.SetBounds(S(104), S(29), S(78), S(24));
            var lblTime = new Label(); lblTime.Text = "时间"; lblTime.TextAlign = ContentAlignment.MiddleLeft;
            lblTime.SetBounds(S(210), S(29), S(40), S(24));
            numHour = new NumericUpDown(); numHour.SetBounds(S(252), S(29), S(48), S(24));
            numHour.Minimum = 0; numHour.Maximum = 23;
            var lblC = new Label(); lblC.Text = ":"; lblC.TextAlign = ContentAlignment.MiddleCenter;
            lblC.SetBounds(S(304), S(29), S(12), S(24));
            numMinute = new NumericUpDown(); numMinute.SetBounds(S(318), S(29), S(48), S(24));
            numMinute.Minimum = 0; numMinute.Maximum = 59;

            btnApply = new Button(); btnApply.Text = "应用计划"; btnApply.SetBounds(S(400), S(26), S(100), S(30));
            btnApply.Click += delegate { ApplySchedule(); };

            btnRemoveTask = new Button(); btnRemoveTask.Text = "删除任务"; btnRemoveTask.SetBounds(S(512), S(26), S(100), S(30));
            btnRemoveTask.Click += delegate { RemoveTask(); };

            lblTask = new Label(); lblTask.Text = "任务: —"; lblTask.TextAlign = ContentAlignment.MiddleLeft;
            lblTask.SetBounds(S(632), S(29), S(158), S(24));

            lblNextRun = new Label(); lblNextRun.Text = "下次运行: —"; lblNextRun.AutoSize = true; lblNextRun.SetBounds(S(15), S(63), S(480), S(20));

            grpSch.Controls.Add(lblD); grpSch.Controls.Add(numDays); grpSch.Controls.Add(lblD2);
            grpSch.Controls.Add(lblTime); grpSch.Controls.Add(numHour); grpSch.Controls.Add(lblC); grpSch.Controls.Add(numMinute);
            grpSch.Controls.Add(btnApply); grpSch.Controls.Add(btnRemoveTask);
            grpSch.Controls.Add(lblTask); grpSch.Controls.Add(lblNextRun);

            // ---- 立即扫描 ----
            var grpRun = new GroupBox();
            grpRun.Text = "立即扫描";
            grpRun.SetBounds(S(12), S(300), S(796), S(64));
            grpRun.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            btnScan = new Button(); btnScan.Text = "立即扫描并生成报告"; btnScan.SetBounds(S(15), S(24), S(180), S(32));
            btnScan.Click += delegate { StartScan(); };

            chkOpen = new CheckBox(); chkOpen.Text = "完成后打开报告"; chkOpen.Checked = true; chkOpen.SetBounds(S(215), S(30), S(200), S(22));

            btnOpenReports = new Button(); btnOpenReports.Text = "打开报告文件夹"; btnOpenReports.SetBounds(S(430), S(26), S(130), S(28));
            btnOpenReports.Click += delegate { OpenReportsFolder(); };

            lblStatus = new Label(); lblStatus.Text = "就绪"; lblStatus.ForeColor = Color.DimGray;
            lblStatus.TextAlign = ContentAlignment.MiddleRight;
            lblStatus.SetBounds(S(560), S(658), S(220), S(20));
            lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            grpRun.Controls.Add(btnScan); grpRun.Controls.Add(chkOpen);
            grpRun.Controls.Add(btnOpenReports);

            // ---- 运行日志 ----
            var grpLog = new GroupBox();
            grpLog.Text = "运行日志（当次）";
            grpLog.SetBounds(S(12), S(376), S(796), S(274));
            grpLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            txtLog = new TextBox();
            txtLog.Multiline = true; txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Both; txtLog.WordWrap = false;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.SetBounds(S(15), S(22), S(766), S(238));
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            grpLog.Controls.Add(txtLog);

            lblLastRun = new Label(); lblLastRun.Text = "上次运行: —";
            lblLastRun.AutoSize = true; lblLastRun.SetBounds(S(15), S(658), S(540), S(20));
            lblLastRun.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            Controls.Add(grpMon); Controls.Add(grpSch); Controls.Add(grpRun); Controls.Add(grpLog);
            Controls.Add(lblLastRun); Controls.Add(lblStatus);

            timer = new Timer(); timer.Interval = 500;
            timer.Tick += delegate { TimerTick(); };
            timer.Start();
        }

        // ---------- 配置 ----------

        void LoadConfigToUi()
        {
            cfg = new Config();
            try
            {
                if (File.Exists(ConfigPath))
                    cfg = json.Deserialize<Config>(File.ReadAllText(ConfigPath));
                if (cfg.Drives == null) cfg.Drives = new List<string>();
            }
            catch { cfg = new Config(); }

            numThreshold.Value = Clamp((decimal)cfg.ThresholdMB, numThreshold);
            numReportKeep.Value = Clamp(cfg.ReportKeepDays, numReportKeep);
            numSnapKeep.Value = Clamp(cfg.SnapshotKeepDays, numSnapKeep);
            numDays.Value = Clamp(cfg.ScheduleDays, numDays);
            numHour.Value = Clamp(cfg.ScheduleHour, numHour);
            numMinute.Value = Clamp(cfg.ScheduleMinute, numMinute);
        }

        decimal Clamp(decimal v, NumericUpDown n)
        {
            if (v < n.Minimum) return n.Minimum;
            if (v > n.Maximum) return n.Maximum;
            return v;
        }

        void LoadDrives()
        {
            clbDrives.BeginUpdate();
            clbDrives.Items.Clear();
            var items = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string label = d.VolumeLabel;
                    if (string.IsNullOrEmpty(label)) label = "本地磁盘";
                    double freeGb = d.AvailableFreeSpace / 1073741824.0;
                    double totalGb = d.TotalSize / 1073741824.0;
                    items.Add(string.Format("{0} [{1}]  余 {2:N0} / 总 {3:N0} GB", d.Name, label, freeGb, totalGb));
                }
            }
            catch { }
            foreach (string L in cfg.Drives)
                if (!items.Exists(s => s.StartsWith(L))) items.Add(L + ": (未就绪或非固定磁盘)");
            items.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string s in items)
            {
                int idx = clbDrives.Items.Add(s);
                string letter = s.Length > 0 ? s.Substring(0, 1) : "";
                if (cfg.Drives.Contains(letter)) clbDrives.SetItemChecked(idx, true);
            }
            clbDrives.EndUpdate();
        }

        void ToggleAllDrives()
        {
            bool all = clbDrives.CheckedItems.Count == clbDrives.Items.Count && clbDrives.Items.Count > 0;
            for (int i = 0; i < clbDrives.Items.Count; i++) clbDrives.SetItemChecked(i, !all);
        }

        bool SaveConfig()
        {
            var sel = new List<string>();
            foreach (object it in clbDrives.CheckedItems)
            {
                string s = it as string;
                if (s != null && s.Length > 0) sel.Add(s.Substring(0, 1));
            }
            if (sel.Count == 0)
            {
                MessageBox.Show("请至少勾选一个要监控的磁盘。", "磁盘增量监控", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            cfg.Drives = sel;
            cfg.ThresholdMB = (double)numThreshold.Value;
            cfg.ReportKeepDays = (int)numReportKeep.Value;
            cfg.SnapshotKeepDays = (int)numSnapKeep.Value;
            cfg.ScheduleDays = (int)numDays.Value;
            cfg.ScheduleHour = (int)numHour.Value;
            cfg.ScheduleMinute = (int)numMinute.Value;
            try { File.WriteAllText(ConfigPath, json.Serialize(cfg), Encoding.UTF8); }
            catch (Exception ex) { AppendLog("[GUI] 保存配置失败: " + ex.Message); return false; }
            lblStatus.Text = "设置已保存";
            return true;
        }

        // ---------- 计划任务 ----------

        void RefreshTaskInfo()
        {
            bool exists = Common.TaskExists();
            lblTask.Text = "任务: " + (exists ? "已注册" : "未注册");
            lblNextRun.Text = "下次运行: " + (exists ? Common.QueryNextRun() : "—（应用计划后注册）");
        }

        bool RunUpdateTask(int days, int hour, int minute)
        {
            string o;
            bool ok = Common.RunUpdateTask(baseDir, days, hour, minute, out o);
            if (!ok && o.Length > 0) AppendLog("[GUI] Update-Task 输出: " + o);
            return ok;
        }

        void ApplySchedule()
        {
            if (!SaveConfig()) return;
            lblStatus.Text = "正在应用计划…";
            if (RunUpdateTask((int)numDays.Value, (int)numHour.Value, (int)numMinute.Value))
            {
                AppendLog(string.Format("[GUI] 计划已更新: 每 {0} 天 {1:00}:{2:00} 运行", (int)numDays.Value, (int)numHour.Value, (int)numMinute.Value));
                RefreshTaskInfo();
                lblStatus.Text = "计划已更新";
            }
            else lblStatus.Text = "计划更新失败";
        }

        void EnsureTask()
        {
            if (!Common.TaskExists())
            {
                AppendLog("[GUI] 计划任务未注册，正在按当前配置注册…");
                RunUpdateTask((int)numDays.Value, (int)numHour.Value, (int)numMinute.Value);
                RefreshTaskInfo();
            }
        }

        void RemoveTask()
        {
            if (!Common.TaskExists())
            {
                lblStatus.Text = "任务未注册";
                AppendLog("[GUI] 计划任务不存在，无需删除。");
                return;
            }
            var r = MessageBox.Show(
                "确定删除计划任务 DiskDailyMonitor 吗？\r\n删除后不再定时扫描。\r\n快照和报告数据全部保留，随时可点\"应用计划\"重新注册。",
                "磁盘增量监控", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            string so = "", se = ""; int code = -1;
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/delete /tn " + Common.TaskName + " /f");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                so = p.StandardOutput.ReadToEnd();
                se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                code = p.ExitCode;
            }
            catch (Exception ex)
            {
                AppendLog("[GUI] 删除任务失败: " + ex.Message);
                lblStatus.Text = "删除失败";
                return;
            }
            AppendLog("[GUI] schtasks: " + (so + se).Trim());
            if (code == 0)
            {
                AppendLog("[GUI] 计划任务已删除，定时扫描停止（数据保留，\"应用计划\"可重新注册）。");
                RefreshTaskInfo();
                lblStatus.Text = "任务已删除";
            }
            else lblStatus.Text = "删除失败";
        }

        // ---------- 扫描 ----------

        void StartScan()
        {
            if (scanWaiting) return;
            if (!SaveConfig()) return;
            EnsureTask();

            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            lastLogLen = -1;

            string so = "", se = "";
            int code = -1;
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/run /tn " + Common.TaskName);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                so = p.StandardOutput.ReadToEnd();
                se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                code = p.ExitCode;
            }
            catch (Exception ex)
            {
                AppendLog("[GUI] 触发任务失败: " + ex.Message);
                lblStatus.Text = "触发失败";
                return;
            }
            AppendLog("[GUI] schtasks: " + (so + se).Trim());
            if (code != 0) { lblStatus.Text = "启动扫描失败"; return; }

            scanWaiting = true;
            scanStart = DateTime.Now;
            btnScan.Enabled = false;
            lblStatus.Text = "扫描中…（看下方日志）";
        }

        void TimerTick()
        {
            TailLog(false);
            if (!scanWaiting) return;

            if (File.Exists(StatePath))
            {
                scanWaiting = false;
                btnScan.Enabled = true;
                RunState st = Common.ReadState(StatePath);
                if (st != null)
                {
                    ShowLastState(st);
                    if (st.Status == "ok")
                    {
                        lblStatus.Text = "完成 ✔";
                        if (chkOpen.Checked && File.Exists(st.ReportPath))
                        {
                            try { Process.Start(st.ReportPath); } catch { }
                        }
                    }
                    else if (st.Status == "skipped") lblStatus.Text = "已跳过";
                    else lblStatus.Text = "出错: " + st.Message;
                }
                else lblStatus.Text = "完成（状态解析失败）";
            }
            else if ((DateTime.Now - scanStart).TotalMinutes > 20)
            {
                scanWaiting = false;
                btnScan.Enabled = true;
                lblStatus.Text = "等待超时（可能已有扫描在运行）";
            }
        }

        // ---------- 日志 / 状态 ----------

        void ShowLastState() { ShowLastState(Common.ReadState(StatePath)); }

        void ShowLastState(RunState st)
        {
            if (st == null) { lblLastRun.Text = "上次运行: —"; return; }
            lblLastRun.Text = "上次运行: " + st.LastRunTime + "  状态: " + st.Status +
                (string.IsNullOrEmpty(st.Message) ? "" : "  (" + st.Message + ")");
        }

        void TailLog(bool initial)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (!fi.Exists)
                {
                    if (lastLogLen != -1) { txtLog.Clear(); lastLogLen = -1; }
                    return;
                }
                if (fi.Length == lastLogLen) return;
                lastLogLen = fi.Length;
                txtLog.Text = Common.ReadShared(LogPath);
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }
            catch { }
        }

        void AppendLog(string line)
        {
            txtLog.AppendText(line + "\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        void OpenReportsFolder()
        {
            try
            {
                Directory.CreateDirectory(ReportDir);
                Process.Start(ReportDir);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
