# 定制版登录、更新与公告

参考 PCL-CE 的服务结构，使用本项目的应用标识和发布源。登录采用微软设备代码流程；更新下载使用 HTTPS 和 SHA-256，退出后以独立更新进程替换 EXE。公告以文本显示。

## 配置与发布

`Plain Craft Launcher 2/PCL.Online.json` 会嵌入 EXE，公开源码中的 `MicrosoftClientId` 留空。在 GitHub 仓库 Settings → Secrets and variables → Actions 中设置 `CLIENT_ID`；手动运行 Build 工作流时，它通过环境变量传给 `scripts/Set-BuildClientId.ps1`，仅修改构建工作区中的配置。缺少或无效的 ID 会使构建提前失败。

本地调试可设置环境变量 `PCL_MS_CLIENT_ID`。本地打包在设置该变量后运行 `./scripts/Build-Launcher.ps1 -Configuration Release`；脚本负责注入、完整 WPF 编译，并在退出时自动恢复源码配置。玩家使用构建后的 EXE 无需设置环境变量。EXE 同目录的同名 JSON 可整体覆盖嵌入配置（应包含全部字段）；环境变量优先级最高，修改后重启生效。

| JSON 字段 | 环境变量 | 含义 |
| --- | --- | --- |
| MicrosoftClientId | PCL_MS_CLIENT_ID | 发布者自己的微软应用 ID；不是 Client Secret |
| ProductId | PCL_PRODUCT_ID | 定制版身份，默认 kamitsubaki-pcl |
| UpdateUrl | PCL_UPDATE_URL | GitHub 备用更新清单。设置环境变量可单独指定更新源 |
| S3UpdateUrl | PCL_S3_UPDATE_URL | 优先使用的 Worker 更新清单地址，可留空关闭 S3 线路 |
| AnnouncementUrl | PCL_ANNOUNCEMENT_URL | 公告清单的公开 HTTPS 地址 |

MicrosoftClientId 由构建流程注入，Minecraft 应用审核与真实登录测试待完成。启动器优先读取 S3 更新清单，清单不可用或未报告新版时检查 GitHub。如果 S3 下载或校验失败，会用同一份清单中的 GitHub 地址下载并校验相同版本。公告地址可自行配置。微软应用需支持个人账号设备代码登录及 Minecraft 服务访问。

更新和公告可以放在已有对象存储的独立目录，例如 `launcher/update.json` 和 `launcher/announcements.json`，不要覆盖整合包更新清单。先发布完整、可独立运行的 EXE，再发布清单。不支持 ZIP、差分包或多文件部署；需要随 EXE 分发依赖的构建不能使用此更新方式。每次发布递增 `ModBase.vb` 中的版本名及 VersionCode，同步 `AssemblyInfo.vb` 的文件版本，并在 `PCL.ReleaseNotes.json` 中添加正式版版本码、版本名和用户可读的更新说明。启动后的升级弹窗和下载前的更新清单共用这些说明。Release 构建自动生成 `update.json`；将它与同次构建的 `PCL-Kamitsubaki.exe` 和 `SHA256SUMS.txt` 一起发布到标签 `v<文件版本>` 的最新正式 Release。早期定制版可设置 `PCL_UPDATE_URL=https://github.com/AiharaYuriko/PCL-Kamitsubaki-Launcher/releases/latest/download/update.json` 并重启后检查更新。

GitHub 正式 Release 发布后，**Sync launcher release to S3** 工作流按标签下载并校验 EXE 和清单，再镜像到 `pcl-mirror/pcl/launcher`。它通过 `s3-sign.kaf.mom` 回读 EXE 验证 SHA-256，最后更新 S3 清单。可在 Actions 按标签手动补跑，旧版本补跑不会覆盖较新的清单。仓库 Actions Secrets 使用 `S3_ACCESS_KEY_ID` 和 `S3_SECRET_ACCESS_KEY`，Actions Variables 使用 `S3_ENDPOINT`、`S3_BUCKET`、`S3_REGION` 和 `S3_PREFIX`。Worker 需允许 `/pcl/launcher/update.json` 和 `/pcl/launcher/releases/v<文件版本>/PCL-Kamitsubaki.exe`。

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

`dotnet run --project Tests/Online.Tests/Online.Tests.vbproj -c Release` 使用模拟网络检查缺少 Client ID、产品隔离、错误哈希拒绝、正确哈希暂存、HTTP 拒绝、公告重要性筛选与去重、关闭联网设置、新旧更新命令，以及 EXE 替换后是否留有备份。

2026-11-07：完整 Windows Release 构建成功，独立 EXE 的主界面、实例设置、整合包更新页面和正常退出已检查；确认 Client ID 已嵌入且源码配置恢复为空。在线服务测试及整合包更新器 11 项回归测试通过。真实微软账号授权、线上下载和运行中启动器的自更新全流程仍待验证。
