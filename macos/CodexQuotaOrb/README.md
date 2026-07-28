# Codex Quota Orb for macOS

原生 AppKit 版 Codex 配额浮球，面向 macOS 13 及以上系统。它使用系统矢量绘制和 Retina 原生缩放，不把低分辨率界面放大，因此在 4K/5K 屏幕上仍保持清晰。

## 当前功能

- 48pt 正圆浮球，默认位于当前主屏幕右下角，可拖动并记忆位置。
- 悬停不展开；单击浮球才展开 252×132pt 周配额卡。
- 右击圆球显示原生菜单，选择“退出插件”即可结束应用。
- 周配额区域可点击，打开可复用的本机 Token 使用详情窗口。
- “全部对话上下文占用”区域可点击，进入“上下文结构 / 项目占比 / 对话明细”；“返回”或 `Esc` 回到上一层。
- 鼠标移出或点击桌面后自动回缩，回缩时严格返回拖动后保存的浮球锚点。
- “置顶”开启后独立常驻；关闭后只跟随 Codex 前台显示。
- 中英文快速切换，不使用白色圆形按钮底。
- 不显示已取消的 5 小时配额板块。
- 登录启动通过 `launchd` 直接运行 `.app` 内可执行文件，不弹出终端窗口。

## 数据口径

| 显示位置 | 数据来源 | 用途 |
| --- | --- | --- |
| 浮球与周配额卡 | Codex Desktop 本地登录态访问的官方配额接口 | 账户周剩余量与重置时间 |
| 详情页上方与下方 Token 统计 | 本机数值型 `token_count` 历史 | 本机累计、今日、本月、本周及历史活动趋势 |
| 中间上下文总占用 | 每个本地会话最后一次 `last_token_usage` 与 `model_context_window` | 全部对话最新占用、容量、剩余容量与汇总占用率 |
| 项目 / 对话占比 | `session_meta` 的会话 ID、`cwd` 与每个会话的最新上下文快照 | 每个项目和独立对话的当前保留上下文占用量 |
| Token 详情页 | `~/.codex/sessions` 与 `~/.codex/archived_sessions` | 本机历史消耗统计 |

这些路径不会混用。上方统计卡与下方活动图负责历史 Token 吞吐；中间卡对每个本地对话只取最后一次 `last_token_usage.input_tokens`，容量取同一事件的 `model_context_window`，再汇总已占用、总容量与占用率。`total_token_usage.input_tokens` 是历史输入吞吐，会反复计入每轮携带的上下文，因此禁止进入上下文卡。项目页和对话页的“总占比”都以全部对话最新上下文占用之和为分母；首行合计直接对照主卡，逐行占比采用两位小数的平衡舍入并在底层总量一致时精确回加到 `100.00%`。若底层合计不一致，合计行保留真实差异并显示警示色。项目路径和会话 ID 只在内存中即时解析，不写入缓存，也不读取消息正文为对话命名。本机 Token 历史绝不用于推算官方配额。程序不会保存访问令牌、账户 ID、原始配额响应、日志内容、提示词或聊天内容。

## 刷新策略

- UI 与显示状态：250ms。
- 官方周配额：通常每 30 秒；接近重置时每 10 秒。
- Codex 会话发生变化：约 1–2 秒内触发刷新，并限制官方请求最短间隔为 2 秒。
- Token 历史：只重新扫描发生变化的 JSONL 文件，未变化文件使用纯数字缓存。

实际显示速度仍受官方配额服务更新频率、网络状态和本地会话落盘时机影响。

## 在 Mac 上构建

需要 Xcode Command Line Tools 和 Swift 5.9 或更新版本。

```bash
cd macos/CodexQuotaOrb
chmod +x scripts/*.sh
./scripts/build-app.sh
```

默认生成同时支持 Apple Silicon 与 Intel 的通用应用：

```text
dist/Codex Quota Orb.app
dist/Codex-Quota-Orb-macOS.dmg
```

只构建当前机器架构：

```bash
./scripts/build-app.sh --native
```

## 安装并登录启动

```bash
./scripts/install-launch-agent.sh
```

脚本把应用复制到 `~/Applications`，并创建当前用户的 LaunchAgent。它不会请求管理员权限。

## 本地验证

构建脚本会自动执行无网络的配额解析自检，也可单独运行：

```bash
"dist/Codex Quota Orb.app/Contents/MacOS/CodexQuotaOrb" --self-test
```

由于签名是本机 ad-hoc 签名，首次从其他电脑下载测试包时可能需要在 Finder 中右键选择“打开”。正式公开分发应使用 Apple Developer ID 签名并完成公证。
