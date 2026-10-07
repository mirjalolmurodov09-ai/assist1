# Deployment, firewall, permissions, privacy

## Build
- Windows: `build\publish.ps1 [-Installer]` → `publish\` (self-contained win-x64: `ClassroomControl.StudentAgent.exe`, `*.dll`, `appsettings.json`, `Resources\`) and `artifacts\ClassroomControl-Student-Setup.exe` (needs Inno Setup 6).
- Any OS: `build/publish.sh` builds and tests core, mock Teacher, tests, simulator. The GitHub Actions workflow (`.github/workflows/build.yml`) builds the WPF app, runs a smoke test, builds the installer and runs an install/uninstall test on Windows.

## Ports / firewall
| Machine | Rule |
|---|---|
| Student | Inbound **UDP**, program = agent exe, remote = **LocalSubnet** (receives the Teacher's unicast discovery reply). Added/removed by the installer with `netsh`. Outbound TCP to the Teacher (default 39501) and UDP 39500 are allowed by default Windows policy; if outbound is blocked, allow them to LocalSubnet only. |
| Teacher | Inbound UDP 39500 and TCP 39501 from LocalSubnet only. |
Nothing is opened to the internet; the agent also refuses public Teacher addresses.

## Administrator rights
Only the **installer** needs them: writing to `Program Files` and adding the firewall rule. The agent runs `asInvoker` (normal user): settings in `%LOCALAPPDATA%\ClassroomControl\StudentAgent`, start-with-Windows via the per-user `HKCU\...\Run` key.

## Start with Windows / exit protection
Default on, visible and changeable in Settings and in the installer. If the Teacher sets "agent must remain active", Exit from the tray asks for the classroom code. This only prevents accidents: no hidden process, no watchdog, no self-restart; Task Manager, uninstall and Windows shutdown always work.

## Data and privacy
Stored: `settings.json` (classroom code encrypted with DPAPI, current user), `logs\` (14 days, secrets redacted). Sent to the Teacher: device id, computer name, student name, IP, OS, agent version; `GetDeviceInfo` (on request) adds Windows user, MAC, CPU, RAM. Not collected: audio, webcam, microphone, browser data, personal files, clipboard, screen (not in this stage).

## Manual acceptance checklist (clean Windows 10/11)
1. Run `ClassroomControl-Student-Setup.exe`; Start Menu entry exists; agent starts; `%LOCALAPPDATA%\ClassroomControl\StudentAgent\settings.json` has a `DeviceId`.
2. Restart Windows → tray icon appears (Run key). 3. Enter classroom code → "Waiting for Teacher approval" → Teacher approves → "Registered", 🟢.
4. Unplug LAN → 🔴, reconnect → 🟢. 5. Switch Wi-Fi → discovery restarts. 6. Teacher sends Ping/GetStatus/GetDeviceInfo.
7. Uninstall: files, shortcuts, Run entry, firewall rule removed; dialog offers to keep or delete config/logs.
Lab: `dotnet run --project tools/AgentSimulator -- real --code CLASS-XXXX-YYYY` simulates PC-01…PC-16 (192.168.1.101–116) against a real Teacher at 192.168.1.10 (see `tools/AgentSimulator/classroom-lab.json`); `mock` runs everything locally.
