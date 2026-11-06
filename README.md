# PCL 神椿市修改版 / PCL Kamitsubaki Custom Edition

第三方基于 [Plain Craft Launcher（PCL）](https://github.com/Meloong-Git/PCL) 独立修改的 Windows Minecraft Java 版启动器，面向神椿市玩家社群的整合包安装、维护和更新。

**原作者：龙腾猫跃及 PCL 贡献者。** [支持 PCL 原作者](https://meloong.com/afd/a/LTCat)。本修改版由 [AiharaYuriko](https://github.com/AiharaYuriko) 独立维护，反馈请提交到[本仓库 Issues](https://github.com/AiharaYuriko/PCL-Kamitsubaki-Launcher/issues)。

## 功能

- 保留上游 Minecraft 安装、实例管理和微软账号登录流程。
- 新增实例内整合包更新页面，以及文件校验、备份、回退和中断恢复逻辑。
- 新增定制版更新和文本公告服务，支持 HTTPS 下载、SHA-256 校验及公告去重。

目前处于开发阶段：Client ID 通过 GitHub Secrets 在构建时注入，Minecraft 应用审核与真实登录测试待完成；更新和公告地址待配置。核心在线服务模拟测试已通过，完整 WPF 构建的 XAML 生成问题仍待解决，暂未发布定制版 EXE。

## Microsoft / Minecraft application review

This independently maintained PCL customization helps our player community manage Minecraft: Java Edition installations and modpack updates on Windows.

| Field | Value |
| --- | --- |
| Public project name | PCL Kamitsubaki Custom Edition / PCL 神椿市修改版 |
| Current Entra application display name | 神椿市PCL中 |
| Maintainer | [AiharaYuriko](https://github.com/AiharaYuriko) |
| Application review details | [Microsoft authentication and review notes](docs/MICROSOFT-APP-REVIEW.md) |

Microsoft authentication signs players into their accounts and retrieves the tokens and Minecraft profiles needed for online play. Players authorize access on Microsoft's sign-in page using the device-code flow with `XboxLive.signin` and `offline_access`. Application details and review status are documented in the [review notes](docs/MICROSOFT-APP-REVIEW.md).

## 源码与构建

```powershell
git clone --recurse-submodules https://github.com/AiharaYuriko/PCL-Kamitsubaki-Launcher.git
cd PCL-Kamitsubaki-Launcher
```

主项目使用 Visual Basic/WPF 与 .NET Framework 4.8，另含 C# 项目和 MeloongCore 子模块。请使用安装了 .NET 桌面开发工作负载、.NET Framework 4.8 targeting pack 及兼容 C# 13 工具链的 Visual Studio/MSBuild。打开 `Plain Craft Launcher 2.sln`，还原 NuGet 依赖后构建。单独使用 `dotnet build` 不是已验证的主程序构建方式。

GitHub 构建工作流仅允许手动触发，不会自动发布 EXE。构建前在仓库 Settings → Secrets and variables → Actions 中设置 `CLIENT_ID`，工作流会将它写入待嵌入的配置文件。公开源码中的 `MicrosoftClientId` 留空；本地调试可设置环境变量 `PCL_MS_CLIENT_ID`。配置和功能限制见：

- [登录、启动器更新与公告配置](Online-Services.md)
- [整合包更新功能与限制](Modpack-Update.md)
- [账号与网络数据说明](docs/DATA-HANDLING.md)

在线服务模拟测试：`dotnet run --project Tests/Online.Tests/Online.Tests.vbproj -c Release`。

## 上游与许可

源码基于 Meloong-Git/PCL 的 `93ede5d`（2.99.0.1 正式版），保留原作者署名和 [LICENCE](LICENCE)。本仓库以源码快照初始化，上游历史可在 [PCL 仓库](https://github.com/Meloong-Git/PCL) 查阅。使用与分发请参阅许可和存储库合理使用指南。第三方依赖保留各自许可，MeloongCore 作为子模块引用。

更新和公告的结构参考 [PCL-CE](https://github.com/PCL-Community/PCL-CE)，采用适配本项目的实现和独立配置。参考文件见 [Online-Services.md](Online-Services.md)。

## 上游原始介绍（以下链接指向原版 PCL）

[![Stars](https://img.shields.io/github/stars/Meloong-Git/PCL?style=flat&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZlcnNpb249IjEiIHdpZHRoPSIxNiIgaGVpZ2h0PSIxNiI+PHBhdGggZD0iTTggLjI1YS43NS43NSAwIDAgMSAuNjczLjQxOGwxLjg4MiAzLjgxNSA0LjIxLjYxMmEuNzUuNzUgMCAwIDEgLjQxNiAxLjI3OWwtMy4wNDYgMi45Ny43MTkgNC4xOTJhLjc1MS43NTEgMCAwIDEtMS4wODguNzkxTDggMTIuMzQ3bC0zLjc2NiAxLjk4YS43NS43NSAwIDAgMS0xLjA4OC0uNzlsLjcyLTQuMTk0TC44MTggNi4zNzRhLjc1Ljc1IDAgMCAxIC40MTYtMS4yOGw0LjIxLS42MTFMNy4zMjcuNjY4QS43NS43NSAwIDAgMSA4IC4yNVoiIGZpbGw9IiNlYWM1NGYiLz48L3N2Zz4=&logoSize=auto&label=Stars&labelColor=444444&color=eac54f)](https://github.com/Meloong-Git/PCL/)
[![Issues](https://img.shields.io/github/issues/Meloong-Git/PCL?style=flat&label=Issues&labelColor=444444&color=1F883D)](https://github.com/Meloong-Git/PCL/issues)
[![哔哩哔哩](https://img.shields.io/badge/动态-BiliBili-00A4DB?style=flat&labelColor=444444&logoSize=auto)](https://space.bilibili.com/11343203/dynamic)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946ce6?style=flat&labelColor=444444&logoSize=auto)](https://meloong.com/afd/a/LTCat)

包含 PCL 的大部分源代码，包括 UI 库、动画模块、下载模块、Minecraft 启动模块等！<br/>
代码并不是实时更新的，它会在每次 PCL 发布更新时同步一次。<br/>
非常感谢社区的大力支持！

<br/>

### 相关资源
- [PCL 下载](https://meloong.com/pcl)：下载可免费使用的正式版 PCL！
- [PCL 功能投票](https://github.com/Meloong-Git/PCL/discussions/2)：来参加投票吧，开发者会优先处理票数较高的项目！
- [帮助文档库](https://github.com/LTCatt/PCL2Help)：PCL 内置帮助文档的存储库
