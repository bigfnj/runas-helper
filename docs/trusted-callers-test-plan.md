# Trusted caller security and test plan

This checklist covers the persistent exact-user SID authorization path. It is
intentionally an integration plan: the security boundary spans Windows process
tokens, a named-pipe DACL, HKLM, the LocalSystem service, and the elevated tray,
so an in-process unit test alone cannot prove the important properties.

Use two ordinary local test users, `AllowedUser` and `DeniedUser`, plus an
administrator. Record each account's SID before testing. Keep the general CLI
gate closed unless a case explicitly says otherwise.

## Release-blocking authorization matrix

| Caller and state | Launch / validate | Gate control | List, read, or kill jobs | Edit trusted users |
|---|---:|---:|---:|---:|
| Installed elevated tray | Allow | Allow | Allow | Allow |
| Installed non-elevated tray, SID not allowlisted, gate closed | Deny | Deny | Deny | Deny |
| `AllowedUser`, gate closed | Allow | Deny | Deny | Deny |
| `DeniedUser`, gate closed | Deny | Deny | Deny | Deny |
| Interactive caller, gate open | Allow | Deny | Deny | Deny |
| Elevated admin using a relocated/renamed client, gate closed and not allowlisted | Deny | Deny | Deny | Deny |

Run every **Allow** launch case once as SYSTEM and once as TrustedInstaller. Run
every **Deny** case for both `launch` and validation so a less-visible verb
cannot bypass the same authorization decision.

## Backward compatibility and default

- Delete `AllowedCallerSids`, restart the service, and confirm local launch
  authorization matches v2.1.5: the installed elevated tray can launch; an
  ordinary CLI caller is denied with the gate closed; opening the gate enables
  it. Remote named-pipe connections are now deliberately rejected regardless of
  the allowlist or gate.
- Repeat with an empty `REG_MULTI_SZ`. Missing and empty policy must be
  equivalent and must not prevent the service from starting.
- Exercise an old client binary against the new service for launch, capture,
  validation, gate status, and job status. No new field may be required in an
  existing request. With the gate open, legacy launch behavior must still work.
  With the gate closed, an allowlisted legacy client must receive launch and
  validation access from its pinned process `TokenUser`, while a non-allowlisted
  legacy client remains denied. The current client additionally exercises the
  optional pipe-token identity cross-check.
- Confirm the existing gate owner-exit reset and `CliGateMinutes` expiry still
  close the gate. A trusted user should continue to work afterward; an
  untrusted user should stop immediately.
- Confirm the ten-slot launch limit and existing timeout behavior are unchanged
  for tray, gate, and trusted-user authorization paths.

## Exact identity and fail-closed behavior

- Add only `AllowedUser`. Confirm `DeniedUser` cannot launch even if both users
  are interactive at the same time.
- Launch from a restricted token for `AllowedUser`, not only a normal token.
  Construct the token so both its normal and restricting access checks include
  `AllowedUser` and so it does not independently pass through `INTERACTIVE`.
  This proves the exact SID is present in the pipe DACL as well as accepted by
  request authorization. Confirm the same restricted token is denied after
  removal.
- Rename `AllowedUser` and confirm access remains, because the SID is unchanged.
- Delete it, recreate the old name, and confirm the newly issued SID is denied.
- Attempt to add BUILTIN\Users, BUILTIN\Administrators, Everyone,
  Authenticated Users, INTERACTIVE, and a local group. Each must be rejected as
  a non-user principal.
- Put invalid text, duplicate SIDs, a well-formed unresolvable SID, and a group
  SID into `AllowedCallerSids` one case at a time, restarting the service after
  each direct registry edit. The service must not crash. Invalid text is
  ignored; canonical SIDs remain visible and removable. A manually inserted
  group SID may make the pipe connectable for that group, but a member must
  still be denied with the gate closed because its exact `TokenUser` differs.
- Make a previously validated domain user temporarily unresolvable, restart the
  service, and confirm its exact SID remains authorized. Durable policy must not
  depend on a domain controller or reverse name lookup being available at boot.
- Verify the service derives the caller SID from the connecting process token.
  Altering JSON fields such as `Source`, command arguments, a PID, SID, or any
  display name must not affect authorization. Connect, write the request, exit,
  and churn processes to force PID reuse; authorization must remain bound to the
  process object pinned at connection time.
- Attempt to connect through `\\hostname\pipe\RunAsHelper` from another machine,
  including as an otherwise trusted account and as a remote administrator. The
  NETWORK deny ACE must reject the connection before request authorization.

## Trusted-user feature coverage

With `AllowedUser` trusted and the general gate closed, verify all existing
execution features:

- SYSTEM and TrustedInstaller targets;
- `validate` and `validate-system`;
- `/capture`, stdout/stderr streaming, and `/timeout:N`;
- every command-line priority setting;
- explicit working directory;
- executable PATH resolution;
- `.cmd`, `.bat`, `.ps1`, `.msc`, `.cpl`, `.reg`, and registered-document host
  resolution;
- quoted paths and multiple forwarded arguments.

Then verify `/jobs`, `/joblog:<id>`, `/kill:<id>`, gate changes, and every policy
management operation are denied for that same caller. Re-run those operations
from the installed elevated tray and confirm they succeed.

## Policy UI and lifecycle

- A non-elevated tray must not be able to add or remove an account. Activating
  the installed tray should make the policy dialog usable.
- The management list shows each trusted account, its status, and its canonical
  SID. **Add local user…** enumerates local accounts and their enabled/disabled
  state. **Find another user…** resolves a valid `DOMAIN\user`, UPN, or supported
  local name without requiring the operator to type a SID.
- Cancel the confirmation warning and confirm no registry or pipe ACL change.
  Accept it and confirm the warning clearly says the account receives arbitrary
  SYSTEM/TrustedInstaller launch access even while the general gate is closed.
- Adding the same SID twice must not create duplicate entries. Removing an
  entry must revoke new requests immediately and survive service/tray restarts.
- Populate 128 distinct valid users, then attempt to add a 129th. The service
  must reject the extra entry without truncating or altering the existing list.
- Remove or disable an account outside RunAS Helper, refresh the dialog, and
  confirm its SID remains identifiable and removable rather than disappearing
  from policy.
- Modify the policy while another launch is active. The existing job should
  follow normal job semantics; new requests should use the new policy. Confirm
  listener refresh does not interrupt unrelated active jobs or leave multiple
  pipe servers accepting different policy snapshots.
- Restart Windows and confirm the trusted list persists while the general gate
  returns to closed.

## Install, upgrade, and removal

- Install over an existing version with a non-empty trusted list. Confirm the
  MSI stops/replaces/restarts the service and preserves
  `HKLM\SOFTWARE\RunAsHelper\AllowedCallerSids`.
- Perform a normal MSI uninstall. Under the current package design the runtime
  value is not MSI-owned, so confirm it remains for a later reinstall and make
  that persistence visible in release notes.
- Reinstall and confirm the preserved policy takes effect without silently
  changing its SIDs.
- Run the repository's elevated `uninstall.py --yes` cleanup path and confirm it
  deletes the entire `HKLM\Software\RunAsHelper` key, including the trusted
  list.

## Audit and regression checks

- Confirm allowed and denied CLI attempts still create the established Windows
  Event Log records and retain `Source: cli`, so existing tray notifications and
  log consumers continue to work.
- Confirm command lines, client PID, account, and outcome remain accurate for a
  trusted caller. Do not log credentials or token material.
- Run parallel launches from the tray, a trusted caller, and a gate-authorized
  caller while adding/removing policy entries. The service must stay responsive,
  enforce the ten-slot limit, and never grant the denied account.
- Stop and restart the service repeatedly with missing, empty, populated, and
  malformed policy. Startup must be deterministic and fail closed.
- Build both Debug and Release, run the normal installation validation, and
  smoke-test the tray in light and dark themes at 100%, 150%, and 200% display
  scaling so the SID column, warning, and management buttons remain usable.
