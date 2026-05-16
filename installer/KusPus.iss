; KusPus installer — Inno Setup script.
; Phase 12 — see TECH_SPEC §30 for the full contract.
;
; Key decisions (when filled in):
;   - PrivilegesRequired=lowest (per-user install)
;   - No HKLM writes
;   - Autostart written by the app on opt-in, not by the installer
;   - tiny.en bundled as external onlyifdoesntexist
;   - Uninstaller leaves user data unless "Also remove my data" is checked
;
; Unsigned in perpetuity (PRD §9.9 / N-11). SmartScreen + Defender + SAC friction
; is documented for testers in docs/INSTALL.md.

#error KusPus.iss not implemented yet (Phase 12).
