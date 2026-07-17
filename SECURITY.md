<!-- Copyright (c) 2026 CaYaDev (https://cayadev.com) | GitHub: CaYatur (https://github.com/CaYatur) | Licensed under the MIT License. -->

# Security Policy

## Supported versions

Security fixes are applied to the latest release and the default branch. Older preview builds may not receive backports.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting feature for `CaYatur/CaYaFix`. If that feature is unavailable, contact CaYaDev through [cayadev.com](https://cayadev.com) and include `CaYaFix security` in the subject.

Do not open a public issue containing credentials, personal diagnostic data, proof-of-concept malware, or a working exploit. Include the affected version, impact, reproduction conditions, and a minimal non-sensitive test case. You should receive an acknowledgement within seven days.

## Security boundaries

CaYaFix runs elevated because Windows repair operations require administrator rights. Elevation does not make recovery data trustworthy. The application therefore rejects unsigned or oversized manifest envelopes, modified backups, path traversal, reparse-point backup paths, unrecognized driver package names, and restore commands outside its executable allowlist.

Support packages are local, consent-gated, and redacted by default. They never intentionally include rollback backups, saved Wi-Fi keys, browser data, documents, or credentials.

The complete design is in [docs/SECURITY-MODEL.md](docs/SECURITY-MODEL.md).
