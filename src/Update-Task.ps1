<#
.SYNOPSIS
  注册/更新 DiskDailyMonitor 计划任务（每 N 天 HH:mm、SYSTEM、错过补跑、唤醒、2小时超时、防重叠）
.DESCRIPTION
  由 DiskMonitorGUI.exe / DiskMonCli.exe（已是管理员）调用，也可手动运行。
  非管理员运行时自动弹一次 UAC 提权重启自身。
  参数缺省时读取 config.json 的 ScheduleDays / ScheduleHour / ScheduleMinute。
  任务动作: powershell -NoProfile -ExecutionPolicy Bypass -File DiskMonitor.ps1 -Rescan
  卸载（管理员）: schtasks /delete /tn DiskDailyMonitor /f
#>
param(
    [int]$DaysInterval = -1,
    [int]$Hour = -1,
    [int]$Minute = -1
)

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$TaskName = 'DiskDailyMonitor'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    try {
        $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"") -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Write-Error 'UAC 被取消或提权失败，无法更新计划任务。'
        exit 1
    }
}

# 读取 config.json 补齐缺省参数
$days = $DaysInterval; $hour = $Hour; $minute = $Minute
$cfgFile = Join-Path $here 'config.json'
if (Test-Path -LiteralPath $cfgFile) {
    try {
        $cfg = Get-Content -LiteralPath $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($days   -lt 1) { $days   = [int]$cfg.ScheduleDays }
        if ($hour   -lt 0) { $hour   = [int]$cfg.ScheduleHour }
        if ($minute -lt 0) { $minute = [int]$cfg.ScheduleMinute }
    } catch { }
}
if ($days   -lt 1)   { $days = 1 }
if ($hour   -lt 0)   { $hour = 1 }
if ($minute -lt 0)   { $minute = 0 }
if ($hour   -gt 23)  { $hour = 23 }
if ($minute -gt 59)  { $minute = 59 }

$engine    = Join-Path $here 'DiskMonitor.ps1'
$action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$engine`" -Rescan"
$trigger   = New-ScheduledTaskTrigger -Daily -DaysInterval $days -At ('{0:00}:{1:00}' -f $hour, $minute)
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

$info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
Write-Output ("PLAN: every {0} day(s) at {1:00}:{2:00}" -f $days, $hour, $minute)
if ($info -and $info.NextRunTime) { Write-Output ("NEXT: {0:yyyy-MM-dd HH:mm}" -f $info.NextRunTime) }
exit 0
