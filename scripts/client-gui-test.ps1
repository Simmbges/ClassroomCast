# 开发辅助：在主窗体 Client 页签填入姓名/IP 并点击连接
param(
    [string]$Name = "测试学生",
    [string]$Ip = "127.0.0.1",
    [string]$Action = "connect"   # connect 或 disconnect
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$winCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty,
    "教室屏幕共享（局域网屏幕直播）")
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
if (-not $win) { throw "未找到主窗体" }

# 切到 Client 页签
$tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "Client 学生端")
$tab = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
if (-not $tab) { throw "未找到 Client 页签" }
($tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Start-Sleep -Milliseconds 500

if ($Action -eq "disconnect") {
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "Client - 断开")
    $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    if (-not $btn) { throw "未找到断开按钮" }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Write-Host "clicked: Client - 断开"
    exit 0
}

# 填两个文本框：顺序为 姓名、老师 IP
$editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
$edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
$textboxes = @()
foreach ($e in $edits) {
    # 排除 NumericUpDown 内部的 Edit（其父为 Spinner）
    $parentIsSpinner = $false
    try {
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $parent = $walker.GetParent($e)
        if ($parent -and $parent.Current.ControlType -eq [System.Windows.Automation.ControlType]::Spinner) { $parentIsSpinner = $true }
    } catch {}
    if (-not $parentIsSpinner) { $textboxes += $e }
}
if ($textboxes.Count -lt 2) { throw "找到的文本框数量不足: $($textboxes.Count)" }

($textboxes[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Name)
($textboxes[1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Ip)
Write-Host "filled: 姓名=$Name IP=$Ip"

$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "Client - 连接")
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
if (-not $btn) { throw "未找到连接按钮" }
($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Write-Host "clicked: Client - 连接"
