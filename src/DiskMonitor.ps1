<#
.SYNOPSIS
  磁盘每日增量监控引擎：扫描 -> 快照 -> 对比 -> HTML 报告（多磁盘）
.DESCRIPTION
  由计划任务 DiskDailyMonitor（SYSTEM）或 DiskMonitorGUI.exe / DiskMonCli.exe 调起。
  读取 config.json 决定监控磁盘与参数；每个磁盘独立快照（snapshots\L-yyyy-MM-dd.csv.gz），
  与该磁盘最近一次其他日期快照对比，合并生成 reports\report-yyyy-MM-dd.html；
  结束时写 state.json 供 GUI/CLI 轮询；last-run.log 记录本次运行全过程（每次运行重写）。
  手动强制重扫: powershell -File DiskMonitor.ps1 -Rescan [-Drives C,E] [-ThresholdMB 5]
#>
param(
    [string]$Drives = '',
    [double]$ThresholdMB = -1,
    [switch]$Rescan
)

$ErrorActionPreference = 'Continue'
$DataDir     = Split-Path -Parent $MyInvocation.MyCommand.Path
$snapDir     = Join-Path $DataDir 'snapshots'
$repDir      = Join-Path $DataDir 'reports'
$logFile     = Join-Path $DataDir 'last-run.log'
$stateFile   = Join-Path $DataDir 'state.json'
$cfgFile     = Join-Path $DataDir 'config.json'
$today       = Get-Date -Format 'yyyy-MM-dd'
$todayReport = Join-Path $repDir "report-$today.html"

$MinSnapBytes = 1MB    # 只快照累计大小达到此值的目录
$MaxRows      = 500    # 报告行数保护上限

New-Item -ItemType Directory -Force -Path $snapDir, $repDir | Out-Null

# ---- 读取配置（缺省值 -> config.json -> 命令行参数覆盖） ----
$cfg = [pscustomobject]@{
    Drives           = @('C')
    ThresholdMB      = 10
    SnapshotMaxDepth = 6
    ReportMaxDepth   = 5
    ReportKeepDays   = 35
    SnapshotKeepDays = 35
    ScheduleDays     = 1
    ScheduleHour     = 1
    ScheduleMinute   = 0
}
if (Test-Path -LiteralPath $cfgFile) {
    try {
        $loaded = Get-Content -LiteralPath $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in @('Drives','ThresholdMB','SnapshotMaxDepth','ReportMaxDepth','ReportKeepDays','SnapshotKeepDays','ScheduleDays','ScheduleHour','ScheduleMinute')) {
            if ($loaded.PSObject.Properties[$p]) { $cfg.$p = $loaded.$p }
        }
    } catch { }
}
if ($Drives -ne '') {
    $cfg.Drives = ($Drives -split '[,，;|]') | ForEach-Object { $_.Trim().TrimEnd(':').ToUpper() } | Where-Object { $_ }
}
if ($ThresholdMB -ge 0) { $cfg.ThresholdMB = $ThresholdMB }
$thresholdBytes = [int64]($cfg.ThresholdMB * 1MB)

function Write-Log([string]$msg) {
    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
    Write-Output $line
}

function Write-State([string]$status, [string]$message, [object]$driveStates) {
    $rp = ''
    if (Test-Path -LiteralPath $todayReport) { $rp = $todayReport }
    $st = [pscustomobject]@{
        Status      = $status
        LastRunTime = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        ReportPath  = $rp
        Message     = $message
        Drives      = $driveStates
    }
    [IO.File]::WriteAllText($stateFile, (ConvertTo-Json $st -Depth 5), [Text.UTF8Encoding]::new($false))
}

function FmtSize([long]$b) {
    $a = [math]::Abs($b)
    if     ($a -ge 1GB) { return '{0:N2} GB' -f ($b / 1GB) }
    elseif ($a -ge 1MB) { return '{0:N1} MB' -f ($b / 1MB) }
    elseif ($a -ge 1KB) { return '{0:N1} KB' -f ($b / 1KB) }
    else                { return '{0:N0} B'  -f $b }
}
function FmtDelta([long]$d) {
    if ($d -ge 0) { return '+' + (FmtSize $d) }
    return '-' + (FmtSize (-$d))
}
function Esc([string]$s) {
    return $s.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;')
}

# ---- 运行开始：重写 last-run.log（GUI 显示"当次"日志） ----
Set-Content -LiteralPath $logFile -Value ('{0}  === 运行开始  磁盘: {1}  阈值: {2} MB  强制重扫: {3}  引擎: PS {4} ===' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), (@($cfg.Drives) -join ','), $cfg.ThresholdMB, [bool]$Rescan, $PSVersionTable.PSVersion.ToString()) -Encoding UTF8

$letters = @()
foreach ($d in @($cfg.Drives)) {
    $L = "$d".Trim().TrimEnd(':').ToUpper()
    if ($L -match '^[A-Z]$' -and (Test-Path -LiteralPath "${L}:\")) { $letters += $L }
    else { Write-Log "跳过无效或不可用的磁盘: $d" }
}
if ($letters.Count -eq 0) {
    Write-Log '没有可监控的磁盘，退出。'
    Write-State 'error' '没有可监控的磁盘' @()
    exit 1
}

# 幂等保护：未指定 -Rescan 且所有选中磁盘今天都已有快照 -> 跳过
if (-not $Rescan) {
    $allHave = $true
    foreach ($L in $letters) {
        if (-not (Test-Path -LiteralPath (Join-Path $snapDir ("$L-$today.csv.gz")))) { $allHave = $false }
    }
    if ($allHave) {
        Write-Log '今日快照已存在且未指定 -Rescan，跳过本次运行。'
        Write-State 'skipped' '今日快照已存在（强制重扫请加 -Rescan）' @()
        exit 0
    }
}

function Scan-Volume([string]$L) {
    $root = "${L}:\"
    Write-Log "开始扫描 $root ..."
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $stack.Push($root)
    $immediate   = @{}
    $childrenMap = @{}
    $allDirs   = [System.Collections.Generic.List[string]]::new()
    $fileCount = [long]0
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        $allDirs.Add($dir)
        $sum = [long]0
        $childList = [System.Collections.Generic.List[string]]::new()
        try {
            $di = [System.IO.DirectoryInfo]::new($dir)
            foreach ($fi in $di.EnumerateFiles()) { $sum += $fi.Length; $fileCount++ }
            foreach ($sdi in $di.EnumerateDirectories()) {
                if ($sdi.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
                $childList.Add($sdi.FullName)
                $stack.Push($sdi.FullName)
            }
        } catch { }
        $immediate[$dir]   = $sum
        $childrenMap[$dir] = $childList
    }
    $cumulative = @{}
    for ($i = $allDirs.Count - 1; $i -ge 0; $i--) {
        $d = $allDirs[$i]
        $t = $immediate[$d]
        foreach ($c in $childrenMap[$d]) { $t += $cumulative[$c] }
        $cumulative[$d] = $t
    }
    $sw.Stop()
    $o = [pscustomobject]@{
        Letter = $L; Root = $root
        AllDirs = $allDirs; Immediate = $immediate; Children = $childrenMap; Cumulative = $cumulative
        FileCount = $fileCount; DirCount = $allDirs.Count; Total = $cumulative[$root]
        ElapsedSec = [int]$sw.Elapsed.TotalSeconds
    }
    Write-Log ("{0} 扫描完成: {1} 个目录 / {2} 个文件 / 共 {3}, 耗时 {4} 秒" -f $root, $o.DirCount, $fileCount, (FmtSize $o.Total), $o.ElapsedSec)
    return $o
}

function Save-Snapshot($scan) {
    $path = Join-Path $snapDir ("{0}-{1}.csv.gz" -f $scan.Letter, $today)
    $tmp  = "$path.tmp"
    $n = 0
    $gzF = $null; $gzS = $null; $w = $null
    try {
        $gzF = [IO.File]::Create($tmp)
        $gzS = [IO.Compression.GZipStream]::new($gzF, [IO.Compression.CompressionLevel]::Fastest)
        $w   = [IO.StreamWriter]::new($gzS, [Text.UTF8Encoding]::new($false))
        foreach ($d in $scan.AllDirs) {
            $sz = $scan.Cumulative[$d]
            if ($sz -lt $MinSnapBytes) { continue }
            if (($d.Split('\').Count - 2) -gt $cfg.SnapshotMaxDepth) { continue }
            $w.Write($d); $w.Write("`t"); $w.WriteLine($sz)
            $n++
        }
        $w.Flush()
    } finally {
        if ($w) { $w.Dispose() } elseif ($gzS) { $gzS.Dispose() } elseif ($gzF) { $gzF.Dispose() }
    }
    Move-Item -LiteralPath $tmp -Destination $path -Force
    Write-Log ("快照已写入: {0}（{1} 个目录, {2}）" -f $path, $n, (FmtSize (Get-Item -LiteralPath $path).Length))
}

function Load-Prev([string]$L) {
    $map = @{}; $date = $null
    $f = Get-ChildItem -LiteralPath $snapDir -Filter ("{0}-*.csv.gz" -f $L) -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne ("{0}-{1}.csv.gz" -f $L, $today) } |
        Sort-Object Name -Descending | Select-Object -First 1
    if ($f) {
        $date = $f.Name.Substring(2, 10)
        $fs = [IO.File]::OpenRead($f.FullName)
        $gz = [IO.Compression.GZipStream]::new($fs, [IO.Compression.CompressionMode]::Decompress)
        $rd = [IO.StreamReader]::new($gz, [Text.UTF8Encoding]::new($false))
        while ($null -ne ($line = $rd.ReadLine())) {
            $tab = $line.LastIndexOf("`t")
            if ($tab -gt 0) { $map[$line.Substring(0, $tab)] = [long]$line.Substring($tab + 1) }
        }
        $rd.Dispose()
    }
    return [pscustomobject]@{ Map = $map; Date = $date }
}

function Get-Cur([string]$p) { if ($script:SCAN.Cumulative.ContainsKey($p)) { return $script:SCAN.Cumulative[$p] } return [long]0 }
function Get-Old([string]$p) { if ($script:PREV.Map.ContainsKey($p))       { return $script:PREV.Map[$p] }       return [long]0 }

function Drill([string]$dir, [int]$depth) {
    if ($script:Rows.Count -ge $MaxRows) { $script:truncated = $true; return }
    $cur   = Get-Cur $dir
    $delta = $cur - (Get-Old $dir)
    $name  = if ($depth -le 1) { $dir } else { Split-Path $dir -Leaf }
    $cls   = if ($delta -ge 0) { 'up' } else { 'down' }
    $script:Rows.Add(('<tr><td class="p" style="padding-left:{0}px">{1}</td><td class="{2}">{3}</td><td class="n">{4}</td></tr>' -f (16 + 24 * ($depth - 1)), (Esc $name), $cls, (FmtDelta $delta), (FmtSize $cur)))
    if ([math]::Abs($delta) -ge $thresholdBytes -and $depth -lt $cfg.ReportMaxDepth) {
        $kids = @()
        if ($script:SCAN.Children.ContainsKey($dir)) { $kids = $script:SCAN.Children[$dir] }
        $ko = foreach ($k in $kids) { [pscustomobject]@{ P = $k; D = (Get-Cur $k) - (Get-Old $k) } }
        foreach ($k in ($ko | Sort-Object { [math]::Abs($_.D) } -Descending)) {
            if ([math]::Abs($k.D) -ge $thresholdBytes) { Drill $k.P ($depth + 1) }
        }
    }
}

# ============ 主流程 ============
try {
    $sectionHtml  = [System.Collections.Generic.List[string]]::new()
    $driveStates  = [System.Collections.Generic.List[object]]::new()
    $totalElapsed = 0

    foreach ($L in $letters) {
        $scan = Scan-Volume $L
        Save-Snapshot $scan
        $prev = Load-Prev $L
        $script:SCAN = $scan
        $script:PREV = $prev
        $script:Rows = [System.Collections.Generic.List[string]]::new()
        $script:truncated = $false

        $deltaBytes = [long]0
        $mode  = 'baseline'
        $dmeta = ''

        if ($prev.Map.Count -gt 0) {
            $mode = 'delta'
            $deltaBytes = $scan.Total - (Get-Old $scan.Root)
            $spanDays = ([datetime]$today - [datetime]$prev.Date).Days
            $dmeta = ('对比 {0} 快照（跨 {1} 天）｜ {2} 盘总变化 <b>{3}</b> ｜ {4} 个目录 / {5} 个文件 ｜ 扫描耗时 {6} 秒' -f $prev.Date, $spanDays, $L, (FmtDelta $deltaBytes), $scan.DirCount, $scan.FileCount, $scan.ElapsedSec)

            $topObjs = foreach ($k in $scan.Children[$scan.Root]) { [pscustomobject]@{ P = $k; D = (Get-Cur $k) - (Get-Old $k) } }
            foreach ($k in ($topObjs | Sort-Object { [math]::Abs($_.D) } -Descending)) {
                if ([math]::Abs($k.D) -ge $thresholdBytes) { Drill $k.P 1 }
            }
        } else {
            $dmeta = ('{0} 盘首日基线（无历史快照可对比），下次运行起出增量 ｜ {1} 个目录 / {2} 个文件 ｜ 总量 {3} ｜ 扫描耗时 {4} 秒' -f $L, $scan.DirCount, $scan.FileCount, (FmtSize $scan.Total), $scan.ElapsedSec)
            $topDirs = $scan.Children[$scan.Root] | ForEach-Object { [pscustomobject]@{ P = $_; S = $scan.Cumulative[$_] } } |
                Sort-Object S -Descending | Select-Object -First 15
            foreach ($t in $topDirs) {
                $pct = if ($scan.Total -gt 0) { $t.S / $scan.Total } else { 0 }
                $script:Rows.Add(('<tr><td class="p">{0}</td><td class="n">—</td><td class="n">{1}（{2:P1}）</td></tr>' -f (Esc $t.P), (FmtSize $t.S), $pct))
            }
        }

        # 已删除目录：上次快照有、本次扫描无（只列父目录仍存在的最上层）
        $delRows = [System.Collections.Generic.List[string]]::new()
        if ($prev.Map.Count -gt 0) {
            $delObjs = @()
            foreach ($k in @($prev.Map.Keys)) {
                if (-not $scan.Cumulative.ContainsKey($k) -and $prev.Map[$k] -ge $thresholdBytes) {
                    $par = Split-Path $k -Parent
                    if ($par -and $scan.Cumulative.ContainsKey($par)) {
                        $delObjs += ,[pscustomobject]@{ P = $k; S = $prev.Map[$k] }
                    }
                }
            }
            foreach ($o in ($delObjs | Sort-Object S -Descending)) {
                $delRows.Add(('<tr><td class="p">{0}</td><td class="down">-{1}</td><td class="n">已删除</td></tr>' -f (Esc $o.P), (FmtSize $o.S)))
            }
        }

        if ($script:Rows.Count -eq 0 -and $delRows.Count -eq 0 -and $mode -eq 'delta') {
            $script:Rows.Add(('<tr><td colspan="3" class="muted">所有目录变化均小于 {0} MB</td></tr>' -f $cfg.ThresholdMB))
        }
        if ($script:truncated) {
            $script:Rows.Add('<tr><td colspan="3" class="muted">（变化条目过多，已截断至 500 行）</td></tr>')
        }

        $delHtml = ''
        if ($delRows.Count -gt 0) {
            $delHtml = "<h4>已删除目录（释放空间）</h4><table>" + ($delRows -join "`n") + "</table>"
        }
        $secHtml = "<h3>$L 盘</h3>`n<div class='meta'>$dmeta</div>`n<table>`n<tr><th>目录</th><th class='r'>变化</th><th class='r'>当前大小</th></tr>`n$($script:Rows -join "`n")`n</table>`n$delHtml"
        $sectionHtml.Add($secHtml)
        $driveStates.Add([pscustomobject]@{
            Drive = $L; Mode = $mode
            TotalBytes = [long]$scan.Total; DeltaBytes = $deltaBytes
            FileCount = $scan.FileCount; DirCount = $scan.DirCount
            ElapsedSec = $scan.ElapsedSec
        })
        $totalElapsed += $scan.ElapsedSec
    }

    $title = "磁盘每日增量报告 $today"
    $allSections = $sectionHtml -join "`n"
    $html = @"
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>$title</title>
<style>
body{font-family:"Microsoft YaHei",system-ui,sans-serif;max-width:960px;margin:24px auto;padding:0 16px;color:#222}
h2{margin-bottom:4px}
h3{margin:26px 0 6px;border-left:4px solid #4a90d9;padding-left:8px}
h4{margin:14px 0 4px;color:#2e7d32}
.meta{color:#666;font-size:13px;margin-bottom:10px}
table{border-collapse:collapse;width:100%;font-size:14px;margin-bottom:8px}
td,th{padding:5px 10px;border-bottom:1px solid #eee}
th{text-align:left;background:#f7f7f7}
th.r{text-align:right}
td.n,td.up,td.down{text-align:right;font-family:Consolas,monospace;white-space:nowrap}
td.up{color:#c62828}
td.down{color:#2e7d32}
tr:hover{background:#fafafa}
.muted{color:#999}
.foot{color:#999;font-size:12px;margin-top:18px}
</style>
</head>
<body>
<h2>$title</h2>
<div class="meta">增量阈值 $($cfg.ThresholdMB) MB ｜ 总扫描耗时 $($totalElapsed) 秒 ｜ 数据目录: $($DataDir)</div>
$allSections
<div class="foot">口径：逻辑文件大小（FileInfo.Length）；已跳过 junction/符号链接；WinSxS 硬链接会重复计数，但每日口径一致，不影响增量判断。</div>
</body>
</html>
"@
    [IO.File]::WriteAllText($todayReport, $html, [Text.UTF8Encoding]::new($false))

    # 清理过期文件（报告与快照保留期分离）
    $cutSnap = (Get-Date).AddDays(-$cfg.SnapshotKeepDays)
    Get-ChildItem -LiteralPath $snapDir -Filter '*.csv.gz' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $cutSnap } | Remove-Item -Force -ErrorAction SilentlyContinue
    $cutRep = (Get-Date).AddDays(-$cfg.ReportKeepDays)
    Get-ChildItem -LiteralPath $repDir -Filter 'report-*.html' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $cutRep } | Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Log "完成: 报告 $todayReport（总耗时 $totalElapsed 秒）"
    Write-State 'ok' '' $driveStates
    exit 0
} catch {
    Write-Log ("出错: {0}" -f $_.Exception.Message)
    Write-State 'error' $_.Exception.Message @()
    exit 1
}
