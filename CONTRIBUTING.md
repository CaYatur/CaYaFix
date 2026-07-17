<!-- Copyright (c) 2026 CaYaDev (https://cayadev.com) | GitHub: CaYatur (https://github.com/CaYatur) | Licensed under the MIT License. -->

# Contributing to CaYaFix

## Development setup

Use Windows 10/11 with the .NET 8 SDK. Fork the repository, create a focused branch, and run:

```powershell
.\tools\validate-repository.ps1
dotnet build .\CaYaFix.sln -c Release -warnaserror
dotnet test .\CaYaFix.Tests\CaYaFix.Tests.csproj -c Release
```

## Repair-action requirements

Every new repair must:

1. Be tied to a concrete diagnostic finding.
2. Declare the lowest accurate risk tier.
3. Create a restorable backup before changing state.
4. Stop if the backup fails.
5. Use `ICommandRunner` with a fixed executable and separate arguments.
6. Validate discovered targets and never interpolate user input into PowerShell.
7. Provide an independent verifier.
8. Include success, failure, cancellation, tampering, and rollback tests.
9. Add matching English and Turkish resource keys.
10. Re-run the exact diagnostic after repair, or document why a historical-only check needs a current-state verifier.

Do not add blanket registry cleaners, driver downloaders, security-control bypasses, credential collection, or destructive disk actions.

## Pull requests

Keep changes scoped, describe risk and rollback behavior, and include test evidence. UI changes must remain usable at 860x640 and 1360x860, use repository SVG icons instead of emoji, preserve keyboard/automation labels, and include actual English application screenshots when the appearance changes.
