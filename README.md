# KusPus

A floating, hotkey-driven, fully on-device speech-to-text utility for Windows. From-scratch rewrite of the macOS app **WhisprFlow / FloatingRecorder**.

Press one hotkey, dictate, release. Transcript is pasted into whatever field had focus, on-device, no cloud.

**Status:** pre-implementation. See [docs/PRD.md](docs/PRD.md) and [docs/TECH_SPEC.md](docs/TECH_SPEC.md) for the v1.0 design contract.

## Documents

| | |
|---|---|
| [docs/PRD.md](docs/PRD.md) | What we are building and why (source of truth for v1.0 scope) |
| [docs/TECH_SPEC.md](docs/TECH_SPEC.md) | How we are building it (prescriptive technical contract) |
| [docs/ROADMAP.md](docs/ROADMAP.md) | What is deferred past v1.0 |
| [docs/BUILD.md](docs/BUILD.md) | Local dev setup |
| [docs/INSTALL.md](docs/INSTALL.md) | End-user install troubleshooting (Phase 12+) |

## Build

See [docs/BUILD.md](docs/BUILD.md). Target stack: C# + WPF + **.NET 10 LTS**, Windows 10 22H2 + Windows 11 (x64).

## License

MIT — see [LICENSE](LICENSE).
