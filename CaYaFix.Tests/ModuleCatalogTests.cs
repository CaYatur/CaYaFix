// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Modules;

namespace CaYaFix.Tests;

public sealed class ModuleCatalogTests
{
    [Fact]
    public void CatalogHasUniqueAndInternallyValidDefinitions()
    {
        var modules = ModuleCatalog.CreateAll();

        Assert.Equal(14, modules.Count);
        Assert.Equal(56, modules.Sum(module => module.Checks.Count));
        Assert.Equal(47, modules.Sum(module => module.Fixes.Count));
        Assert.Equal(8, modules.Sum(module => module.LiveTests.Count));
        Assert.Equal(modules.Count, modules.Select(module => module.Info.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var module in modules)
        {
            Assert.NotEmpty(module.Checks);
            Assert.Equal(module.Checks.Count, module.Checks.Select(check => check.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(module.Fixes.Count, module.Fixes.Select(fix => fix.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(module.Checks, check => Assert.Equal(module.Info.Id, check.ModuleId));
            Assert.All(module.Fixes, fix => Assert.Equal(module.Info.Id, fix.ModuleId));
            Assert.All(module.LiveTests, test => Assert.Equal(module.Info.Id, test.ModuleId));

            var fixIds = module.Fixes.Select(fix => fix.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var checkIds = module.Checks.Select(check => check.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(module.Playbooks, playbook =>
            {
                Assert.Equal(module.Info.Id, playbook.ModuleId);
                Assert.All(playbook.CheckIds, checkId => Assert.Contains(checkId, checkIds));
                Assert.All(playbook.PreferredFixIds, fixId => Assert.Contains(fixId, fixIds));
            });
        }
    }
}
