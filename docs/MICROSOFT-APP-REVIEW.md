# Microsoft / Minecraft application review information

- Public project name: **PCL Kamitsubaki Custom Edition / PCL 神椿市修改版**.
- Current Microsoft Entra display name: **神椿市PCL中**.
- Project homepage and source: https://github.com/AiharaYuriko/PCL-Kamitsubaki-Launcher
- Maintainer: https://github.com/AiharaYuriko
- Public enquiries: https://github.com/AiharaYuriko/PCL-Kamitsubaki-Launcher/issues

## Purpose

This Windows desktop application independently customizes [PCL](https://github.com/Meloong-Git/PCL), originally developed by 龙腾猫跃 and its contributors. It manages Minecraft: Java Edition installations, instances and community modpack updates.

Microsoft/Minecraft API access is requested to authenticate players, check Minecraft entitlements and retrieve their profiles for the Microsoft-account game flow.

## Authentication implementation

The inherited PCL implementation uses OAuth device authorization with `XboxLive.signin offline_access`, followed by Xbox Live, XSTS and Minecraft Services authentication. Players enter credentials on Microsoft's verification page. This desktop flow does not require a client secret. Tokens maintain the account session and enable Minecraft profile retrieval and launch in that account's context.

- [Microsoft login implementation](../Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb): `MsLoginStep1New`, `MsLoginStep1Refresh` and the subsequent Xbox/Minecraft steps.
- [Device-code login dialog and polling](../Plain%20Craft%20Launcher%202/Pages/PageLaunch/MyMsgLogin.xaml.vb).
- [Application configuration template](../Plain%20Craft%20Launcher%202/PCL.Online.json).
- [Custom configuration, update and announcement service](../Plain%20Craft%20Launcher%202/Modules/ModOnline.vb).

The launcher also retains upstream offline/local accounts. This application request covers the Microsoft-authenticated account flow.

## Development status

The Client ID is supplied in the application review form and injected during builds from the repository's `CLIENT_ID` secret. The public configuration leaves this field empty. Minecraft Services approval and end-to-end sign-in testing are pending. Build and test status is tracked in the [README](../README.md).

See [data handling](DATA-HANDLING.md) for account and network information, and [configuration](../Online-Services.md) for publisher settings.
