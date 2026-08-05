# ![AAEmu](https://i.imgur.com/NFDY376.png)

Open source server software for ArcheAge written in `.NET`.

## Start Here

- Audience: Contributors, players, and testers
- Last verified against: `develop` on August 5, 2026
- Prerequisites: None
- Preferred local development: [Aspire Development Guide](Aspire-Development-Guide)
- Manual setup fallback: [Installation & Setup](Installation-&-Setup)
- Docker setup alternative: [Docker Installation Guide](Docker-Installation-Guide)
- Troubleshooting first stop: [Mini troubleshoot guide](Mini-troubleshoot-guide)
- Need help: [Getting Help](Getting-Help)

## Documentation Map

### Project Status

- [Project Status](Project-Status) — fork milestone plan, M0/M1 progress
- [Golden Route — Solzreed](Golden-Route-Solzreed) — the curated M1 opening progression
- [Quest Test Harness](Quest-Test-Harness) — scenario harness, game-data graphs, quality gate

### Getting Started

- [Dependencies and Downloads](Dependencies-and-Downloads)
- [Aspire Development Guide](Aspire-Development-Guide)
- [Installation & Setup](Installation-&-Setup)
- [Docker Installation Guide](Docker-Installation-Guide)

### Configuration

- [Working with the Config.json files and server listings](Working-with-the-Config.json-files-and-server-listings)

### Operations and Troubleshooting

- [Mini troubleshoot guide](Mini-troubleshoot-guide)
- [FAQ](FAQ)

### Help and Support

- [Getting Help](Getting-Help)
- [Asking for Help on Discord](Asking-for-Help-on-Discord)
- [Asking for Help on GitHub Discussions](Asking-for-Help-on-GitHub-Discussions)
- [Help Us Help You](Help-Us-Help-You)

### Project Reference

- [Components](Components)
- [Code Terminology](Code-Terminology)
- [Developer Notes](Developer-Notes)
- [Classes](Classes)
- [Client](Client)
- [Server](Server)

### Legacy Community Guides

- [Alternative Guide](https://docs.google.com/document/d/1XfZR6zb9-n2oldu1NB9e5_9eLEwam8r2wAt3vKA5Vpo/edit)
- [Alternative Guide (Russian/на русском языке)](https://docs.google.com/document/d/1O_v6dTyvv99tBgjvoGXUReKUj5azmzn7mdAD4FsUfKM/edit?usp=sharing)

### Contributing Docs

- [Documentation Maintenance](Documentation-Maintenance)

## Platform Changes (February 2026)

- `.NET Aspire` AppHost is available and is now the preferred contributor workflow.
- Login server public network uses ASP.NET Core Kestrel.
- Login server game server listings are configured via `GameServers`
  configuration (not MySQL `game_servers`).
- `Config.Local.json` is loaded last and overrides all other game server
  configuration files.

## Related

- [Aspire Development Guide](Aspire-Development-Guide)
- [Dependencies and Downloads](Dependencies-and-Downloads)
- [Installation & Setup](Installation-&-Setup)
- [FAQ](FAQ)
