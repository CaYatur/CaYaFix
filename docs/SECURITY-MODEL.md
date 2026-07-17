<!-- Copyright (c) 2026 CaYaDev (https://cayadev.com) | GitHub: CaYatur (https://github.com/CaYatur) | Licensed under the MIT License. -->

# Security Model

## Goals

- A diagnosis must not change Windows state.
- A failed or missing backup must prevent its repair.
- Recovery data must not become an elevated arbitrary-command or arbitrary-file primitive.
- A support package must minimize personal data and never include credentials by design.
- Cancellation, command failure, reboot, and verification failure must leave an auditable session.

## Controls

| Threat | Control |
|---|---|
| Executable search-path hijacking | Fixed executable allowlist resolved to absolute Windows System32 paths |
| Argument injection | `ProcessStartInfo.ArgumentList`; discovered targets remain separate arguments |
| Unbounded subprocess | Per-command timeout, process-tree termination, output capture bound, cancellation token |
| Manifest modification | HMAC-SHA-256 signature with a current-user DPAPI-protected key |
| Torn manifest/signature update | One signed envelope written with write-through, explicitly flushed to disk, then atomically replaced |
| Backup modification | SHA-256 file or directory digest verified before restore |
| Path traversal | Canonical root containment and session identifier validation |
| Junction/symlink abuse | Reparse points rejected throughout backup and session paths |
| Oversized recovery input | Manifest, directory bytes, file counts, command arguments, and restore metadata are bounded |
| Arbitrary driver package | Published driver name restricted to `oem<digits>.inf` |
| Malicious restore metadata | Signed manifest plus restore-executable allowlist and argument limits |
| Target crossover | Per-finding parameter dictionaries; target-required actions fail closed without their exact key |
| Broad route deletion | Only diagnosed invalid/conflicting persistent IPv4 route tuples are accepted; active and unrelated routes are left unchanged |
| Partial repair | Backup content flushed to disk first, signed write-ahead recovery intent, apply once, action verifier, exact diagnostic recheck, automatic rollback |
| Irreversible system mutation | No automated action unless the pre-change state has a complete, verified recovery path |
| High-risk change | Aggressive tier requires explicit consent and restore point |
| Sensitive support archive | Local consent, deterministic redaction, narrow file allowlist, no rollback data |
| Cross-user local data disclosure | Protected ACL restricted to current user, SYSTEM, and local Administrators; startup verifies the effective allow-list |
| Privileged screenshot write | Capture mode writes only to the ACL-protected fixed LocalAppData capture directory; it accepts no output path |

## Trust assumptions

Windows, the local administrator account, Microsoft-supplied system tools, and the installed CaYaFix binary are trusted. A fully compromised administrator account, the same user running malicious code, or a compromised kernel is outside the model. DPAPI protection is scoped to the current Windows user; another account cannot use the session signing key by simply reading the file. ProgramData and LocalAppData roots have inheritance removed and grant access only to the current user, SYSTEM, and local Administrators. The signature is a local tamper-evidence control, not a remote identity certificate.

## Recovery behavior

Unsigned, malformed, oversized, path-inconsistent, reparse-point, or signature-invalid sessions are retained on disk for manual inspection but are not shown as recoverable. Immediately before a repair can mutate state, backup files are explicitly flushed and a signed write-ahead action record is durably written with the verified backup reference. If the process ends before durable verification, the next launch identifies the interrupted action, displays a persistent Recovery Center banner, and blocks new scans and repairs until the action is rolled back. A modified backup fails closed before any restore command runs. Bundle members are restored in reverse order and individually verified. Per-action recovery reloads the trusted on-disk envelope and never trusts the mutable UI copy. Diagnostics may report problems for which CaYaFix intentionally offers no automated change when Windows cannot expose an exact rollback path.

## Privacy behavior

Logs do not persist subprocess arguments. Support-package text replaces usernames, computer and device names, user-profile paths, email addresses, Windows SIDs, GUIDs, MAC addresses, SSIDs/profile names, device/instance identifiers, serial values, Wi-Fi key content, passwords, passphrases, secrets, access tokens, API keys, and IP addresses. The archive contains only bounded diagnostic text, a redacted report, up to five recent redacted application logs, and a privacy notice. It excludes rollback backups, documents, browser data, saved wireless keys, and microphone samples, and should still be reviewed before sharing.

Microphone tests process and play samples only in bounded memory, clear both capture buffers after success or cancellation, and retain only aggregate metrics. Network testing uses fixed displayed endpoints, timeouts, a 10 MB speed-test cap, and a 64 KiB HTTP-probe cap. CaYaFix contains no telemetry client or automatic upload path.
