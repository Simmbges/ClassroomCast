# 教室屏幕共享 - Windows 防火墙放行脚本（老师端电脑运行一次）
# 需要管理员权限：右键"以管理员身份运行 PowerShell"后执行本脚本。
# 默认放行 TCP 9527 端口（仅"专用网络"配置文件生效）。如修改过软件端口，请同步修改此处。

$ErrorActionPreference = "Stop"
$port = 9527
$ruleName = "教室屏幕共享（TCP $port）"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "请以管理员身份运行本脚本（右键 PowerShell -> 以管理员身份运行）。" -ForegroundColor Red
    exit 1
}

# 删除同名旧规则后重建（保持幂等）
netsh advfirewall firewall delete rule name="$ruleName" | Out-Null
netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=TCP localport=$port profile=private,domain

Write-Host "已放行 TCP $port 端口（专用/域网络）。学生现在可以连接了。" -ForegroundColor Green
