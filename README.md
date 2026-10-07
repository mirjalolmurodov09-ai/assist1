# Classroom Control — Student Agent (stage 3)

Layout: `StudentAgent.Core` (protocol, security, services, ViewModels — cross-platform, fully tested), `StudentAgent` (WPF views, tray, startup),
`StudentAgent.TeacherMock` (real TLS/UDP Teacher used by tests and the simulator), `StudentAgent.Tests`, `tools/AgentSimulator`, `installer/`, `build/`, `docs/`.

- Protocol: [docs/PROTOCOL.md](docs/PROTOCOL.md) · Deployment/firewall/privacy: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)
- Tests: `dotnet test StudentAgent.Tests`
