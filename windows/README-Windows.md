# Codex Quota Orb for Windows

适用于 Windows 10/11，使用原生 WinForms 绘制并启用 Per-Monitor DPI V2，在 4K 与多屏环境下按目标显示器的实际缩放重新渲染。

## 快速安装

1. 解压 `Codex-Quota-Orb-Windows.zip`。
2. 右键 `Install.ps1`，选择“使用 PowerShell 运行”。
3. 安装完成后浮球会立即启动，并默认添加当前用户登录启动项。

如果只想临时运行，双击 `Start Codex Quota Orb.vbs`，不会出现蓝色 PowerShell 后台窗口。

## 操作

- 单击圆球：展开周配额与重置时间。
- 点击周配额区域：打开本机 Codex Token 使用详情。
- 鼠标移出或点击桌面：自动回缩到原位置。
- `EN` / `中`：切换语言。
- `T`：切换始终置顶。
- `Esc`：回缩；圆球状态下再次按 `Esc` 退出。

## 卸载

运行 `Uninstall.ps1` 删除登录启动项。关闭浮球后，可删除：

```text
%LOCALAPPDATA%\CodexQuotaOrb
```

## 数据与隐私

- 浮球配额读取 Codex 官方周使用量接口。
- Token 详情只统计本机 `.codex` 会话中的数字事件。
- 不保存访问令牌、提示词、聊天正文或原始配额响应。
