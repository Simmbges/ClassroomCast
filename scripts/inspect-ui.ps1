# 开发辅助：列出主窗体全部控件的名称/值文本  用法: inspect-ui.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$winCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty,
    "教室屏幕共享（局域网屏幕直播）")
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
if (-not $win) { throw "未找到主窗体" }

$all = [System.Windows.Automation.Condition]::TrueCondition
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $all)
foreach ($i in $items) {
    $c = $i.Current
    $name = $c.Name
    $value = ""
    try { $value = $i.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    if (-not $value) { try { $value = $i.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value } catch {} }
    if ($name -or $value) {
        Write-Host ("[{0}] {1} {2}" -f $c.ControlType.ProgrammaticName, $name, $(if ($value) { "= $value" } else { "" }))
    }
}
