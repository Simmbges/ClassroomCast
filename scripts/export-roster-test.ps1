# 开发辅助：自动化"导出名单"流程
# 用法: export-roster-test.ps1 -Mode empty   （无学生时点击，验证提示对话框）
#       export-roster-test.ps1 -Mode full -SavePath <csv全路径>  （选路径保存并确认）
param(
    [string]$Mode = "full",
    [string]$SavePath = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Mouse {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x02, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
        mouse_event(0x04, 0, 0, 0, UIntPtr.Zero); // LEFTUP
    }
}
'@
[Mouse]::SetProcessDPIAware() | Out-Null

function Find-Window([string]$title, [int]$timeoutMs = 8000) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $title)
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

# UIA 定位按钮中心，用真实鼠标点击（InvokePattern 在点击后立即弹模态框的场景会超时）
function Invoke-Button($window, [string]$buttonName) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $buttonName)
    $btn = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $btn) { throw "未找到按钮：$buttonName" }
    [Mouse]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 200
    $r = $btn.Current.BoundingRectangle
    [Mouse]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}

# 1. 在主窗体上点击"导出名单"
$main = Find-Window "教室屏幕共享（局域网屏幕直播）" 5000
if (-not $main) { throw "未找到主窗体" }
Invoke-Button $main "导出名单"
Write-Host "clicked: 导出名单"

if ($Mode -eq "empty") {
    # 2a. 等待"提示"对话框（空名单警告），点"确定"
    $dlg = Find-Window "提示" 8000
    if (-not $dlg) { throw "未出现空名单提示对话框" }
    Write-Host "text: " ($dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text))).Current.Name)
    Invoke-Button $dlg "确定"
    Write-Host "EMPTY-PASS"
    exit 0
}

# 2b. 等待保存对话框
$saveDlg = Find-Window "导出学生名单" 8000
if (-not $saveDlg) { throw "未出现保存对话框" }

# 文件名输入框：对话框中唯一的 Edit 控件
$editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
$edit = $saveDlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
if (-not $edit) { throw "未找到文件名输入框" }
($edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($SavePath)
Write-Host "set filename: $SavePath"

# 保存按钮：名称以"保存"开头，用鼠标点击
$allCond = [System.Windows.Automation.Condition]::TrueCondition
$items = $saveDlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $allCond)
$saved = $false
foreach ($i in $items) {
    if ($i.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $i.Current.Name -like "保存*") {
        [Mouse]::SetForegroundWindow([IntPtr]$saveDlg.Current.NativeWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 200
        $r = $i.Current.BoundingRectangle
        [Mouse]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        $saved = $true
        break
    }
}
if (-not $saved) { throw "未找到保存按钮" }
Write-Host "clicked: 保存"

# 3. 等待"导出成功"对话框并确认
$okDlg = Find-Window "导出成功" 8000
if (-not $okDlg) { throw "未出现导出成功对话框" }
$texts = $okDlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)))
foreach ($t in $texts) { Write-Host "msg: $($t.Current.Name)" }
Invoke-Button $okDlg "确定"
Write-Host "FULL-PASS"
