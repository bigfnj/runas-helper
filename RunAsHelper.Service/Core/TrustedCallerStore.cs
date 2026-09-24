using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RunAsHelper.Service.Core;

/// <summary>
/// Machine-wide policy for accounts that may use the command-line launch API
/// without opening the broad INTERACTIVE gate. Additions must resolve to a user
/// account, after which the canonical SID is the durable authorization identity.
/// </summary>
internal sealed class TrustedCallerStore
{
    internal const string RegistryPath = @"SOFTWARE\RunAsHelper";
    internal const string RegistryValue = "AllowedCallerSids";
    internal const int MaxTrustedCallers = 128;

    private readonly ILogger _logger;
    private readonly object _sync = new();
    private HashSet<string> _allowed = new(StringComparer.Ordinal);

    internal TrustedCallerStore(ILogger logger)
    {
        _logger = logger;
        _allowed = Load();
    }

    internal SecurityIdentifier[] Snapshot()
    {
        lock (_sync)
        {
            return _allowed
                .OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => new SecurityIdentifier(value))
                .ToArray();
        }
    }

    internal bool Contains(SecurityIdentifier? sid)
    {
        if (sid is null) return false;
        lock (_sync) return _allowed.Contains(sid.Value);
    }

    internal bool TryAdd(
        string candidate,
        out string canonicalSid,
        out bool changed,
        out string error)
    {
        canonicalSid = "";
        changed = false;
        error = "";

        if (!TryValidateUserSid(candidate, out var sid, out error))
            return false;

        canonicalSid = sid.Value;
        lock (_sync)
        {
            if (_allowed.Contains(canonicalSid)) return true;

            if (_allowed.Count >= MaxTrustedCallers)
            {
                error = $"The trusted caller list is limited to {MaxTrustedCallers} accounts.";
                return false;
            }

            var updated = new HashSet<string>(_allowed, StringComparer.Ordinal)
            {
                canonicalSid,
            };
            if (!TryPersist(updated, out error)) return false;

            _allowed = updated;
            changed = true;
            return true;
        }
    }

    internal bool TryRemove(
        string candidate,
        out string canonicalSid,
        out bool changed,
        out string error)
    {
        canonicalSid = "";
        changed = false;
        error = "";

        // Removal intentionally does not require the account to remain resolvable:
        // an account can be deleted after it was admitted, and administrators must
        // still be able to remove its persisted SID. Syntax remains strict.
        if (!TryParseCanonicalSid(candidate, out var sid, out error))
            return false;

        canonicalSid = sid.Value;
        lock (_sync)
        {
            if (!_allowed.Contains(canonicalSid)) return true;

            var updated = new HashSet<string>(_allowed, StringComparer.Ordinal);
            updated.Remove(canonicalSid);
            if (!TryPersist(updated, out error)) return false;

            _allowed = updated;
            changed = true;
            return true;
        }
    }

    private HashSet<string> Load()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(RegistryPath, writable: false);
            object? value = key?.GetValue(
                RegistryValue,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);

            if (value is null) return result;
            if (value is not string[] configured)
            {
                _logger.LogWarning(
                    "Ignoring {Path}\\{Value}: expected REG_MULTI_SZ.",
                    RegistryPath, RegistryValue);
                return result;
            }

            foreach (string candidate in configured)
            {
                if (TryParseCanonicalSid(candidate, out var sid, out string error))
                {
                    if (!result.Contains(sid.Value)
                        && result.Count >= MaxTrustedCallers)
                    {
                        _logger.LogWarning(
                            "Ignoring trusted caller SID '{Sid}': policy is limited to {Limit} entries.",
                            candidate, MaxTrustedCallers);
                        continue;
                    }

                    // Do not re-resolve a persisted SID here. Domain and Entra
                    // directories may be offline at service start; exact TokenUser
                    // equality remains authoritative after add-time user validation.
                    result.Add(sid.Value);
                }
                else
                    _logger.LogWarning(
                        "Ignoring invalid trusted caller SID '{Sid}': {Reason}",
                        candidate, error);
            }
        }
        catch (Exception ex)
        {
            // Fail closed: a missing or unreadable policy is an empty policy and
            // therefore preserves the legacy gate behavior.
            _logger.LogError(ex, "Could not read trusted caller policy; using an empty allowlist.");
        }

        _logger.LogInformation(
            "Loaded {Count} trusted command-line caller(s).", result.Count);
        return result;
    }

    private static bool TryPersist(HashSet<string> values, out string error)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(RegistryPath, writable: true);
            if (key is null)
            {
                error = $"Could not open HKLM\\{RegistryPath} for writing.";
                return false;
            }

            string[] ordered = values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            key.SetValue(RegistryValue, ordered, RegistryValueKind.MultiString);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not save trusted caller policy: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidateUserSid(
        string candidate,
        out SecurityIdentifier sid,
        out string error)
    {
        if (!TryParseCanonicalSid(candidate, out sid, out error))
            return false;

        if (!IsUserAccount(sid))
        {
            error = "The SID does not resolve to a user account.";
            return false;
        }

        return true;
    }

    private static bool TryParseCanonicalSid(
        string candidate,
        out SecurityIdentifier sid,
        out string error)
    {
        sid = null!;
        error = "";

        if (string.IsNullOrEmpty(candidate))
        {
            error = "A SID is required.";
            return false;
        }

        try
        {
            sid = new SecurityIdentifier(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or SystemException)
        {
            error = "The value is not a valid Windows SID.";
            return false;
        }

        // Do not silently trim, normalize, or reinterpret policy input. This keeps
        // the registry representation deterministic and exact comparisons simple.
        if (!string.Equals(candidate, sid.Value, StringComparison.Ordinal))
        {
            error = $"Use the canonical SID form '{sid.Value}'.";
            return false;
        }

        return true;
    }

    private static unsafe bool IsUserAccount(SecurityIdentifier sid)
    {
        byte[] bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);

        fixed (byte* pSidBytes = bytes)
        {
            IntPtr pSid = (IntPtr)pSidBytes;
            uint nameLength = 0;
            uint domainLength = 0;

            _ = NativeMethods.LookupAccountSidW(
                null, pSid, null, ref nameLength, null, ref domainLength, out _);
            if (nameLength == 0) return false;

            char[] name = new char[checked((int)nameLength)];
            char[] domain = new char[Math.Max(1, checked((int)domainLength))];
            fixed (char* pName = name)
            fixed (char* pDomain = domain)
            {
                return NativeMethods.LookupAccountSidW(
                           null, pSid,
                           pName, ref nameLength,
                           pDomain, ref domainLength,
                           out int sidType)
                       && sidType == NativeMethods.SID_NAME_USE_USER;
            }
        }
    }
}
