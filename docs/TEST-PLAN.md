<!-- Copyright (c) 2026 CaYaDev (https://cayadev.com) | GitHub: CaYatur (https://github.com/CaYatur) | Licensed under the MIT License. -->

# Test Plan

## Automated release gates

1. Repository policy validation: localization parity, XML/JSON/SVG validity, XAML static-resource resolution, MIT headers, exact catalog counts, executable allowlist coverage, emoji/SVG policy, forbidden process patterns, secret signatures, README metadata, and real-PNG checks for the capture workflow.
2. Release build with compiler warnings treated as errors.
3. Unit and integration tests with a five-minute hang timeout.
4. Self-contained `win-x64` publish.
5. CodeQL analysis and dependency updates.
6. Process-isolated soak repetitions before a tagged release, plus scheduled process-tree working-set, handle-count, orphan-testhost, and sustained-growth gates.

## Covered negative cases

- Backup returns null or throws: apply is never called.
- Dry run: backup, apply, and verifier are never called; every catalog repair emits its bounded command/operation plan.
- Aggressive action without consent or restore point: blocked.
- Aggressive action with consent but a failed backup: blocked without exception.
- Manifest bytes or signature changed: session rejected.
- Backup bytes changed: restore command never called.
- Session or backup path escapes its root: rejected.
- Manifest envelope is truncated, oversized, unsigned, or has an invalid signature: rejected.
- Session or backup path contains a reparse point: rejected.
- Invalid driver identifier: no command executed.
- Apply failure, verification failure, and cancellation: automatic rollback attempted.
- Process termination between backup, apply, and verification: a signed write-ahead recovery intent remains discoverable and undoable.
- Interrupted repair detected at startup: a persistent UI banner is shown and new scan/repair entry points remain blocked until rollback succeeds.
- Backup files and the signed manifest envelope are explicitly flushed before the repair apply stage.
- Post-restart exact diagnostic failure: verified backups restored in reverse order; rollback failure stays pending.
- Exact diagnostic remains present after a weak action verifier: rollback attempted.
- Two targeted findings use the same repair ID: each receives only its own target.
- Single-action rollback: only the selected backup is restored.
- Full rollback: actions are restored in reverse order.
- English, Turkish, German, fully lost, and malformed/overflowing ping parsing; unsupported Windows UI languages retain English UI fallback.
- Persistent-route targets round-trip within a strict count/length grammar; malformed addresses, prefixes, indices, extra fields, and command-like text are rejected.
- User, network, Wi-Fi key, password, secret, and token redaction.
- Email, SID, GUID, device-name, and user-path redaction.
- Privileged screenshot mode cannot accept or derive a caller-controlled output directory.
- Command output, diagnostic findings, live-test detail, coalesced UI-console queue, threshold file, manifest, session-directory history, support-package input, and backup directory bounds.
- Recovery Center renders at most 100 action-bearing sessions, prioritizes interrupted/reboot-pending work, and disables undo for sessions with nothing recoverable.
- Duplicate modules, checks, fixes, or broken playbook references.
- A repair without a complete rollback path, including component-store mutation or Store package re-registration.
- Exhausted repair tiers enter the handoff state; the repair button is hidden and report/support next steps remain available.

## Manual Windows matrix

| Area | Minimum matrix |
|---|---|
| OS | Windows 10 22H2; Windows 11 current and previous feature update |
| Display | 100%, 125%, 150%, and 200% DPI; 860x640 through ultrawide |
| Network | Ethernet, Wi-Fi, no gateway, captive portal, VPN, proxy, IPv4-only, IPv6 dual stack |
| Audio | Speakers, USB headset, Bluetooth headset, HDMI, virtual endpoint, no microphone permission |
| Locale | English, Turkish, and one unsupported Windows UI language to confirm English fallback |
| Recovery | Safe, moderate, pending-reboot, individual undo, full reverse-order undo, tampered data |
| Cancellation | Every long-running component-store scan, throughput, ping, audio, and driver operation |
| UI motion | Ping pulse, microphone waveform, speaker animation, cancellation, reduced window width, and no icon fallback glyphs |
| Screenshots | English forced; dashboard, findings, and active live-test stages; PNG at least 1000x600 |

## Soak protocol

Run `tools\soak-test.ps1 -Iterations 50` for pull requests that change concurrency, process handling, persistence, or audio capture. Run 200 process-isolated iterations and an overnight interactive live-test loop for release candidates. Alternate cancellation timing, disconnected devices, network loss, low disk space, and reboot-pending sessions. Record the commit, Windows build, device inventory, iteration count, failures, peak process memory/handle count, retained session size, and generated TRX files.

No finite test plan proves the absence of every defect. A release is blocked by any reproducible crash, unbounded growth, unsigned recovery acceptance, change without backup, verifier false-positive, untranslated resource, or secret-scan finding.
