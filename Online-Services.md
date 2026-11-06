# 定制版登录、更新与公告

参考 PCL-CE 的服务结构，使用本项目的应用标识和发布源。登录采用微软设备代码流程；更新下载使用 HTTPS 和 SHA-256，退出后以独立更新进程替换 EXE，保留 `.update-backup`。公告以文本显示。

## 配置与发布

`Plain Craft Launcher 2/PCL.Online.json` 会嵌入 EXE，公开源码中的 `MicrosoftClientId` 留空。在 GitHub 仓库 Settings → Secrets and variables → Actions 中设置 `CLIENT_ID`；手动运行 Build 工作流时，它通过环境变量传给 `scripts/Set-BuildClientId.ps1`，仅修改构建工作区中的配置。缺少或无效的 ID 会使构建提前失败。

本地调试可设置环境变量 `PCL_MS_CLIENT_ID`。本地打包可先设置该变量，再运行 `./scripts/Set-BuildClientId.ps1`，构建后恢复配置中的空值，不要提交注入后的文件。玩家使用构建后的 EXE 无需设置环境变量。EXE 同目录的同名 JSON 可整体覆盖嵌入配置（应包含全部字段）；环境变量优先级最高，修改后重启生效。

| JSON 字段 | 环境变量 | 含义 |
| --- | --- | --- |
| MicrosoftClientId | PCL_MS_CLIENT_ID | 发布者自己的微软应用 ID；不是 Client Secret |
| ProductId | PCL_PRODUCT_ID | 定制版身份，默认 kamitsubaki-pcl |
| UpdateUrl | PCL_UPDATE_URL | 更新清单的公开 HTTPS 地址 |
| AnnouncementUrl | PCL_ANNOUNCEMENT_URL | 公告清单的公开 HTTPS 地址 |

MicrosoftClientId 由构建流程注入，Minecraft 应用审核与真实登录测试待完成；两个 URL 待发布者填写。微软应用需支持个人账号设备代码登录及 Minecraft 服务访问。

更新和公告可以放在已有对象存储的独立目录，例如 `launcher/update.json` 和 `launcher/announcements.json`，不要覆盖整合包更新清单。先发布完整、可独立运行的 EXE，再发布清单。不支持 ZIP、差分包或多文件部署；需要随 EXE 分发依赖的构建不能使用此更新方式。每次发布同时递增 `ModBase.vb` 中的 VersionCode 和更新清单中的 versionCode；当前正式版为 415、快照版为 416。

更新清单示例（哈希必须替换为实际 EXE 的 SHA-256）：

```json
{
  "productId": "kamitsubaki-pcl",
  "versionCode": 417,
  "versionName": "2.99.0.2",
  "important": false,
  "changelog": "本次更新说明",
  "downloadUrl": "https://example.com/launcher/Launcher.exe",
  "sha256": "实际文件的64位十六进制SHA256"
}
```

公告清单示例：

```json
{
  "productId": "kamitsubaki-pcl",
  "announcements": [
    {"id": "2026-11-07-01", "title": "维护通知", "text": "通知正文", "important": true}
  ]
}
```

公告 ID 不重复使用；展示记录保存在启动器目录下的 `PCL/CustomAnnouncements.json`。遵循设置中的“所有公告 / 重要公告 / 关闭”。更新遵循“自动下载 / 提示 / 重要更新 / 关闭”，手动检查不受自动检查关闭的影响。自动下载完成后退出安装，不主动重启游戏或启动器。暂存文件位于 `PCL/CustomUpdate`；安装目录需可写。整合包导出使用独立的 `Custom-Latest.exe` 缓存。

本实现是针对当前 VB/.NET Framework 项目的适配，并非 PCL-CE apiv2 清单协议兼容层。尚未接入 PCL 官方联机配置（ServerConfig），不影响本次更新和公告功能的独立配置。

## 参考源码

- [PCL-CE 构建时注入 CLIENT_ID](https://github.com/PCL-Community/PCL-CE/blob/c2a3e740ffa00dccdcd7b7e84edb68d3354c773c/.github/workflows/reusable-build.yml)
- [更新调度、校验与替换](https://github.com/PCL-Community/PCL-CE/blob/c2a3e740ffa00dccdcd7b7e84edb68d3354c773c/PCL.Launcher.Core/Updates/UpdateManager.cs)
- [公告读取与去重](https://github.com/PCL-Community/PCL-CE/blob/c2a3e740ffa00dccdcd7b7e84edb68d3354c773c/PCL.Launcher.Core/Updates/AnnouncementService.cs)

## 验证

`dotnet run --project Tests/Online.Tests/Online.Tests.vbproj -c Release` 使用模拟网络检查缺少 Client ID、产品隔离、错误哈希拒绝、正确哈希暂存、HTTP 拒绝、公告重要性筛选与去重、关闭联网设置。不会运行或替换真实启动器。未验证真实微软账号授权、线上下载以及 Windows 下的退出替换全流程；发布前需在可构建 WPF 的环境完成整机验证。
