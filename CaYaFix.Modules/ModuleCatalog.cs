// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Core;
using CaYaFix.Modules.Audio;
using CaYaFix.Modules.Network;
using CaYaFix.Modules.Other;

namespace CaYaFix.Modules;

public static class ModuleCatalog
{
    public static IReadOnlyList<IModuleDefinition> CreateAll() =>
    [
        new NetworkModule(),
        new AudioModule(),
        new WindowsUpdateModule(),
        new PrinterModule(),
        new BluetoothModule(),
        new DiskStorageModule(),
        new SystemIntegrityModule(),
        new StoreAppsModule(),
        new TimeSyncModule(),
        new StartupPerformanceModule(),
        new CameraPrivacyModule(),
        new UsbDevicesModule(),
        new WindowsSearchModule(),
        new DisplayGraphicsModule(),
        new BootRecoveryModule()
    ];
}
