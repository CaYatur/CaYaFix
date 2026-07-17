// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CaYaFix.Core;

public static class DataDirectorySecurity
{
    public static void CreateRestricted(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CaYaFix data ACLs require Windows.");
        }

        var fullPath = Path.GetFullPath(path);
        if (HasReparsePointInExistingPath(fullPath))
        {
            throw new SecurityException("The CaYaFix data path cannot contain a reparse point.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ?? throw new SecurityException("The current Windows user SID is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var allowed = new[] { currentUser, system, administrators }
            .DistinctBy(sid => sid.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var directory = new DirectoryInfo(fullPath);
        try
        {
            if (!directory.Exists)
            {
                // Create first, then harden ACLs. Some hosts reject Create(DirectorySecurity)
                // when the parent ACL does not yet grant the elevated token full control.
                Directory.CreateDirectory(fullPath);
                directory.Refresh();
            }

            if (!directory.Exists)
            {
                throw new SecurityException("The CaYaFix data directory could not be created.");
            }

            ApplyRestrictedAcl(directory, currentUser, administrators, allowed);
            Verify(directory, allowed, currentUser, administrators);
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (SystemException exception)
        {
            throw new SecurityException(
                "The CaYaFix data directory could not be restricted safely: " + exception.Message,
                exception);
        }
    }

    private static void ApplyRestrictedAcl(
        DirectoryInfo directory,
        SecurityIdentifier currentUser,
        SecurityIdentifier administrators,
        IReadOnlyList<SecurityIdentifier> allowed)
    {
        // Elevated processes may not own a leftover folder from a prior install/user.
        // Take ownership before replacing the discretionary ACL.
        TrySetOwner(directory, currentUser);
        TrySetOwner(directory, administrators);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        try
        {
            security.SetOwner(currentUser);
        }
        catch (InvalidOperationException)
        {
            security.SetOwner(administrators);
        }
        catch (UnauthorizedAccessException)
        {
            security.SetOwner(administrators);
        }

        foreach (var sid in allowed)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        FileSystemAclExtensions.SetAccessControl(directory, security);
    }

    private static void TrySetOwner(DirectoryInfo directory, SecurityIdentifier owner)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            FileSystemAclExtensions.SetAccessControl(directory, security);
        }
        catch (SystemException)
        {
            // Ownership may already be correct, or a later ACL write will succeed as admin.
        }
    }

    private static void Verify(
        DirectoryInfo directory,
        IReadOnlyCollection<SecurityIdentifier> allowed,
        SecurityIdentifier currentUser,
        SecurityIdentifier administrators)
    {
        directory.Refresh();
        if (!directory.Exists || HasReparsePointInExistingPath(directory.FullName))
        {
            throw new SecurityException("The restricted CaYaFix data directory is not a regular directory.");
        }

        var allowedValues = allowed.Select(sid => sid.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = FileSystemAclExtensions.GetAccessControl(
            directory,
            AccessControlSections.Access | AccessControlSections.Owner);
        var rules = applied.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var owner = applied.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new SecurityException("The restricted CaYaFix data directory owner is unavailable.");

        // Elevated creation can leave BUILTIN\Administrators as owner under Windows owner policy.
        if (!owner.Equals(currentUser) && !owner.Equals(administrators))
        {
            throw new SecurityException(
                "The CaYaFix data directory owner must be the current user or local Administrators.");
        }

        if (rules.Any(rule => rule.IsInherited))
        {
            throw new SecurityException("The CaYaFix data directory still has inherited ACL entries.");
        }

        if (rules.Any(rule => rule.AccessControlType != AccessControlType.Allow))
        {
            throw new SecurityException("The CaYaFix data directory must not contain deny ACL entries.");
        }

        var principalsWithFullControl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!allowedValues.Contains(sid.Value))
            {
                throw new SecurityException(
                    "The CaYaFix data directory grants access to an unexpected principal: " + sid.Value);
            }

            if (!HasEffectiveFullControl(rule.FileSystemRights) ||
                (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) !=
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) ||
                rule.PropagationFlags != PropagationFlags.None)
            {
                throw new SecurityException(
                    "The CaYaFix data directory ACL rule is not FullControl with container/object inheritance.");
            }

            principalsWithFullControl.Add(sid.Value);
        }

        foreach (var required in allowedValues)
        {
            if (!principalsWithFullControl.Contains(required))
            {
                throw new SecurityException(
                    "The CaYaFix data directory is missing FullControl for a required principal: " + required);
            }
        }
    }

    private static bool HasEffectiveFullControl(FileSystemRights rights)
    {
        // Windows may surface FullControl with additional generic/high bits depending on OS version.
        if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
        {
            return true;
        }

        var value = (int)rights;
        return value == -1 || (value & 0x1F01FF) == 0x1F01FF;
    }

    private static bool HasReparsePointInExistingPath(string path)
    {
        FileSystemInfo? current = Directory.Exists(path) ? new DirectoryInfo(path) : new DirectoryInfo(path).Parent;
        while (current is not null)
        {
            try
            {
                if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
        return false;
    }
}
