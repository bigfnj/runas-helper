using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RunAsHelper.Core;

/// <summary>A Windows user name resolved to its canonical security identifier.</summary>
internal sealed record ResolvedWindowsAccount(string AccountName, string Sid);

/// <summary>A local account suitable for display in the trusted-caller picker.</summary>
internal sealed record LocalWindowsAccount(
    string AccountName,
    string Sid,
    bool Enabled,
    string Description,
    string? ResolutionError = null)
{
    public bool CanSelect => Sid.Length != 0 && ResolutionError is null;
    public string Status => ResolutionError is not null
        ? "Unavailable"
        : Enabled ? "Enabled" : "Disabled";
}

/// <summary>
/// Windows account discovery used by the trusted-caller UI. Names are always
/// resolved through LSA to a SID, and only identities reported as SidTypeUser are
/// accepted. The service remains the final authority when policy is saved.
/// </summary>
internal static class WindowsAccountResolver
{
    private const int NerrSuccess        = 0;
    private const int ErrorMoreData      = 234;
    private const int ErrorNoneMapped    = 1332;
    private const int MaxPreferredLength = -1;
    private const int FilterNormalAccount = 0x0002;
    private const uint UserAccountDisabled = 0x0002;

    private enum SidNameUse
    {
        User = 1,
        Group,
        Domain,
        Alias,
        WellKnownGroup,
        DeletedAccount,
        Invalid,
        Unknown,
        Computer,
        Label,
        LogonSession,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Password;
        public uint PasswordAge;
        public uint Privilege;
        [MarshalAs(UnmanagedType.LPWStr)] public string? HomeDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ScriptPath;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(
        string? serverName,
        int level,
        int filter,
        out IntPtr buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountName(
        string? systemName,
        string accountName,
        IntPtr sid,
        ref uint sidSize,
        StringBuilder? referencedDomainName,
        ref uint referencedDomainNameSize,
        out SidNameUse use);

    /// <summary>
    /// Enumerates this computer's local user accounts and resolves each one to a SID.
    /// An account that Windows lists but cannot resolve remains visible (and disabled)
    /// so a discovery failure is not mistaken for an empty machine.
    /// </summary>
    public static IReadOnlyList<LocalWindowsAccount> EnumerateLocalUsers()
    {
        var users = new List<LocalWindowsAccount>();
        IntPtr resume = IntPtr.Zero;

        do
        {
            IntPtr buffer = IntPtr.Zero;
            int result = NetUserEnum(
                null, 1, FilterNormalAccount, out buffer, MaxPreferredLength,
                out int entriesRead, out _, ref resume);

            try
            {
                if (result != NerrSuccess && result != ErrorMoreData)
                    throw new Win32Exception(result, $"Windows could not enumerate local users (error {result}).");

                int itemSize = Marshal.SizeOf<UserInfo1>();
                for (int i = 0; i < entriesRead; i++)
                {
                    var info = Marshal.PtrToStructure<UserInfo1>(IntPtr.Add(buffer, checked(i * itemSize)));
                    if (string.IsNullOrWhiteSpace(info.Name)) continue;

                    string requestedName = $"{Environment.MachineName}\\{info.Name}";
                    bool enabled = (info.Flags & UserAccountDisabled) == 0;
                    if (TryResolveUser(requestedName, out var account, out string error))
                    {
                        users.Add(new LocalWindowsAccount(
                            account!.AccountName,
                            account.Sid,
                            enabled,
                            info.Comment ?? string.Empty));
                    }
                    else
                    {
                        users.Add(new LocalWindowsAccount(
                            requestedName,
                            string.Empty,
                            enabled,
                            info.Comment ?? string.Empty,
                            error));
                    }
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }

            if (result != ErrorMoreData) break;
        }
        while (true);

        users.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.AccountName, right.AccountName));
        return users;
    }

    /// <summary>Resolves an account name and accepts it only when Windows says it is a user.</summary>
    public static bool TryResolveUser(
        string accountName,
        out ResolvedWindowsAccount? account,
        out string error)
    {
        account = null;
        error = string.Empty;
        accountName = accountName.Trim();
        if (accountName.Length == 0)
        {
            error = "Enter a Windows account name.";
            return false;
        }

        uint sidSize = 0;
        uint domainSize = 0;
        LookupAccountName(null, accountName, IntPtr.Zero, ref sidSize, null, ref domainSize, out SidNameUse use);
        int firstError = Marshal.GetLastWin32Error();
        if (sidSize == 0)
        {
            error = firstError == ErrorNoneMapped
                ? $"Windows could not find the account \"{accountName}\"."
                : $"Windows could not resolve \"{accountName}\": {new Win32Exception(firstError).Message}";
            return false;
        }

        IntPtr sidBuffer = Marshal.AllocHGlobal(checked((int)sidSize));
        try
        {
            var domain = new StringBuilder(checked((int)Math.Max(domainSize, 1)));
            if (!LookupAccountName(
                    null, accountName, sidBuffer, ref sidSize, domain, ref domainSize, out use))
            {
                int lookupError = Marshal.GetLastWin32Error();
                error = $"Windows could not resolve \"{accountName}\": {new Win32Exception(lookupError).Message}";
                return false;
            }

            if (use != SidNameUse.User)
            {
                error = $"\"{accountName}\" is a {FriendlySidType(use)}, not a user account. " +
                        "Only individual users can be trusted.";
                return false;
            }

            var sid = new SecurityIdentifier(sidBuffer);
            string canonicalName = accountName;
            try
            {
                canonicalName = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            }
            catch (IdentityNotMappedException)
            {
                // LookupAccountName already resolved and typed the identity. Retain the
                // caller's spelling if the reverse lookup disappears in the meantime.
            }

            account = new ResolvedWindowsAccount(canonicalName, sid.Value);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(sidBuffer);
        }
    }

    /// <summary>Best-effort reverse lookup used to render persisted SIDs.</summary>
    public static bool TryResolveSid(
        string sidValue,
        out ResolvedWindowsAccount? account,
        out string error)
    {
        account = null;
        error = string.Empty;

        try
        {
            var sid = new SecurityIdentifier(sidValue);
            var ntAccount = (NTAccount)sid.Translate(typeof(NTAccount));
            if (!TryResolveUser(ntAccount.Value, out account, out error))
                return false;

            if (!string.Equals(account!.Sid, sid.Value, StringComparison.OrdinalIgnoreCase))
            {
                account = null;
                error = "The account name resolved to a different SID.";
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            error = "The stored value is not a valid Windows SID.";
            return false;
        }
        catch (IdentityNotMappedException)
        {
            error = "The account no longer resolves on this computer.";
            return false;
        }
        catch (SystemException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string FriendlySidType(SidNameUse use) => use switch
    {
        SidNameUse.Group or SidNameUse.Alias or SidNameUse.WellKnownGroup => "group",
        SidNameUse.Domain => "domain",
        SidNameUse.Computer => "computer account",
        SidNameUse.DeletedAccount => "deleted account",
        _ => "non-user security principal",
    };
}
