# Scan the codebase for outbound URLs (Uri / HttpClient targets) that are not in
# the egress allowlist defined in TECH_SPEC §10.2 / PRD §10.2.
#
# Phase 11 — pre-commit + CI hook.
#
# Allowlist (v1.0):
#   - https://huggingface.co/
#   - https://ingest.sentry.io/   (only when CrashReportsOptedIn)

throw "verify-egress-allowlist.ps1 — not implemented yet (Phase 11)."
