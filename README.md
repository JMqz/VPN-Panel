# VPN Panel

A Windows app for the WireSock WireGuard client (`wiresock-client.exe`): dark UI, tray icon, server list, per-app split tunneling, and a connection status based on the real WireGuard handshake. WPF on .NET Framework 4.8 (part of Windows), no dependencies besides WireSock. The UI is in Portuguese.

## Sharing it with other people

Send them **`dist\VPN Panel Setup.exe`** (one file, about 300 KB). The installer:

- copies the app to `C:\Program Files\VPN Panel\VpnPanel.exe`, a folder only admins can change, so the no-UAC task can only ever start the real app;
- adds **VPN Panel** to the Start menu (all users) and, optionally, to the desktop;
- registers it in Settings → Apps → Installed apps, with an uninstaller;
- updates in place: running a newer setup over an older install replaces the exe and keeps servers and settings.

On first open the app walks them through setup:

1. **Install WireSock:** the step has a download button and ticks itself once WireSock is installed (no restart needed).
2. **Add a server:** pick or drag in the `.conf` from their VPN provider. "Como conseguir?" explains where Proton and Mullvad generate it.

Windows asks for admin permission (UAC) only on the first launch. After that the app opens through a scheduled task with no prompt (option "Abrir sem pedir permissão").

**Uninstall** (Settings → Apps, or `VpnPanel.exe --uninstall`): disconnects and removes the scheduled tasks, shortcuts, registry entry and the Program Files folder. Servers and settings in `%APPDATA%\VPN Panel` are kept unless the user turns on "Apagar meus servidores e configurações".

Neither exe is code-signed, so SmartScreen may warn on first run ("Mais informações → Executar assim mesmo"). `dist\VpnPanel.exe` also runs on its own, without installing (portable).

## Where things are

| Path | What |
|---|---|
| `dist\VPN Panel Setup.exe` | Installer to hand out |
| `dist\VpnPanel.exe` | The app (portable copy; the installed one is in `C:\Program Files\VPN Panel`) |
| `%APPDATA%\VPN Panel\configs\*.conf` | Imported servers (contain private keys) |
| `%APPDATA%\VPN Panel\settings.json` | Active server and options |
| `%APPDATA%\VPN Panel\locations.json` | Cached country/city of each server endpoint |
| `%APPDATA%\VPN Panel\wiresock.log` | WireSock output from the last connection |
| `src\` | Source: `App.cs`, `MainWindow.cs`, `Dialogs.cs`, `Setup.cs`, `MainWindow.xaml`, `Theme.xaml`, `app.manifest`, `setup.manifest`, `build.ps1` |

## Tabs

- **Início:** the setup steps until WireSock and a server exist. Then the connect button:
  - gray = off;
  - amber spinner = connecting or reconnecting;
  - green = connected (handshake done);
  - amber "Sem resposta" = the server stopped answering.

  Below the status it says what is protected: "Todo o tráfego do PC", "Só Discord passa pela VPN" or "Tudo, exceto ...". It also shows uptime, the server with its country and city, and your public IP. Failures show a banner in plain Portuguese (bad key, server unreachable, network driver...) with actions such as **Tentar de novo**, **Trocar servidor** or **Ver detalhes**.
- **Servidores:** cards with a country badge and city (from the endpoint, via ipinfo.io). Click to use one; when connected, it switches right away. Add with the button or drag `.conf` files in; files added to the folder by hand show up on their own. Rename and delete (to the Recycle Bin) on each card.
- **Apps:** Todos / Só estes / Todos, exceto. Add from running apps (with icons), by browsing for a program, or by name. Changes save automatically; when connected, a banner offers **Reconectar** to apply them.
- **Opções:** reconnect if the connection drops (up to 3 tries, on by default), block the internet if the VPN drops (kill switch), start with Windows, connect on open, open without the UAC prompt, log, data folder, quit.

Closing the window keeps the app in the tray. The tray icon is green when connected, amber when there's no response and gray when off. Its menu shows the status and has connect/disconnect, open, and quit.

## How it works

- **Elevation:** the exe is `asInvoker`. Without admin rights and without arguments, it runs the scheduled task **VPN Panel - Abrir** (highest privileges, through the Task Scheduler COM API) if the task points to this exe. Otherwise it relaunches itself with `runas` (UAC). The elevated copy creates or updates the task. Installed in Program Files, only an admin can replace the exe the task starts. Turn off "Abrir sem pedir permissão" to remove the task.
- **Start with Windows:** task **VPN Panel** (logon + 10 s, highest privileges, `VpnPanel.exe --tray`).
- **Connect:** `cmd.exe /c wiresock-client.exe run -config <file> -log-level info [-network-lock enabled] > wiresock.log`, so the tunnel survives closing the app. Disconnect kills the process.
- **Status:** "Conectado" requires a WireGuard handshake. The app follows `wiresock.log` for `Handshake initiation sent` / `Handshake response received`:
  - **15 s** without a response: shows **Sem resposta**.
  - **60 s** without a response: if the connection had worked before, the app reconnects (auto-reconnect). Otherwise it stops the tunnel and says the server didn't answer, so a kill switch never leaves the internet blocked without the user knowing.

  The client exiting within 10 s counts as a failure (the banner explains the logged error); a later exit triggers auto-reconnect. Tunnels started outside the app (e.g. from a `.bat` file) have no log to check and show as connected.
- **Split tunneling:** writes `AllowedApps` / `DisallowedApps` in `[Peer]` and leaves every other line byte-for-byte unchanged.
- **Single instance:** mutex `Global\VpnPanelWireSock`; a second launch signals `Global\VpnPanelWireSockShow`.

## Building

```
powershell -NoProfile -ExecutionPolicy Bypass -File src\build.ps1
```

Produces `dist\VpnPanel.exe` and `dist\VPN Panel Setup.exe` (the installer is also attached to each GitHub Release). The setup is the same sources with entry point `VpnPanel.SetupProgram`, the app embedded as a resource, and an admin manifest. Compiles with `csc.exe` from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319` (C# 5: no `$"..."`, `?.`, `=>` members). XAML and the icon are embedded and loaded with `XamlReader`; events are wired in code by `x:Name`. To update an installed copy after building, run the new setup ("Reinstalar"/"Atualizar").

## Roadmap

- Code signing (no SmartScreen warning)
- Run as a Windows service so it connects before login
