# Account and network data handling

## Accounts

Players enter Microsoft credentials on Microsoft's verification page in the device-code flow. Authentication tokens, account identifiers and Minecraft profile information are handled by the inherited PCL account implementation. Refresh tokens support subsequent sign-in.

The application Client ID is injected during builds from GitHub Secrets; the public configuration leaves it empty. The device-code flow uses a public client and requires no client secret. The ID is included in the built application and its authorization requests.

## Networking

Depending on the selected feature, the launcher contacts Microsoft/Xbox/Minecraft services, game/resource download services and mirrors, mod platforms, and services used by optional upstream features. Their operators receive the corresponding requests and ordinary network information such as IP addresses.

The custom modpack updater uses the HTTPS source selected for an instance. Launcher updates and announcements use publisher-configured HTTPS endpoints, currently blank. Announcement IDs are recorded locally in `PCL/CustomAnnouncements.json`. The custom update/announcement code does not send Microsoft authentication tokens to these endpoints.

## Support

Use this repository's Issues for public enquiries. Remove tokens, credentials, private server addresses and personal information before attaching logs or settings. Microsoft account and consent management should be performed through Microsoft's own account interfaces.
