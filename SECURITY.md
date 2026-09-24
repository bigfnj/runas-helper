# Security policy

## Reporting

Open a [private security advisory](https://github.com/bigfnj/runas-helper/security/advisories/new)
rather than a public issue. This is a hobby project maintained by one person, so
expect a reply in days rather than hours.

## What this tool is

RunAS Helper deliberately launches processes as `NT SERVICE\TrustedInstaller`
and `NT AUTHORITY\SYSTEM`. Obtaining SYSTEM from an administrator account is the
entire feature, not a flaw. Windows does not treat the boundary between
Administrator and SYSTEM as a security boundary, and neither does this project.

## In scope

- Anything that lets a caller reach the pipe and launch a process **without**
  at least one intended authorization path: the installed-and-elevated tray
  identity, the caller's exact user SID in the trusted-caller list, or an
  administrator having opened the CLI gate.
- Anything that lets a caller use a client-supplied name, SID, `Source` field,
  group membership, or other self-asserted identity in place of the user SID
  obtained by the service from the connecting process token.
- Anything that allows a network-logon token to reach the service pipe. Remote
  SMB named-pipe use is not supported; authorization is local-machine only.
- Anything that lets a caller other than the installed elevated tray open the
  CLI gate, change the trusted-caller policy, enumerate/read jobs, or terminate
  jobs. A caller trusted for launching is deliberately **not** trusted for these
  administrative operations.
- Anything that turns a non-user SID, group, or broad principal into a trusted
  caller through the management API. The API accepts exact Windows user SIDs
  only; the backing registry value is administrator-owned, and authorization is
  still exact `TokenUser` equality rather than group membership.
- A way to make the service launch a target other than the one the caller asked
  for, for example through the client-side document-association rewrite.
- A gate that outlives its owning tray or its `CliGateMinutes` deadline.
- A trusted-caller removal that still authorizes new launch or validation
  requests after the policy change has succeeded.
- Anything in the MSI that grants more than a per-machine install into
  `%ProgramFiles%\RunAsHelper` needs.

## Out of scope

**The CLI gate is a session-wide grant. This is documented, intended behaviour
and reports about it will be closed as such.** The pipe ACL includes the
`INTERACTIVE` SID, so while an administrator has the gate open, any process in
the interactive session can launch as TrustedInstaller, including non-elevated
processes and processes belonging to standard users. The gate exists so that
unelevated scripts and automation can use the service without each caller
carrying its own elevation. It is off by default, only an elevated tray can open
it, it is revoked when that tray exits, and it expires after `CliGateMinutes`
(default 30). Use the trusted-caller list instead when persistent access should
be limited to selected accounts.

**An explicitly trusted caller receiving SYSTEM or TrustedInstaller launch
access while the general gate is closed is also intended behaviour.** Adding an
account is an administrator's durable grant of the product's complete launch and
validation feature set, including arbitrary command lines, output capture,
timeouts, priority, working directory, document resolution, and both target
accounts. The tray presents that warning before adding the account. This grant
does not include gate, job, or policy controls.

The service gets the client's PID from the named-pipe kernel API, opens that
process once at connection time, and reads `TokenUser`, path, and elevation while
the same process handle pins the process object. It never accepts a client-supplied
PID or SID, and numeric PID reuse cannot substitute another process after the
handle is open. The current client additionally requests named-pipe
`Identification`; when Windows exposes the pipe token, its user SID must agree
with the pinned process token. Restricted or older clients for which pipe
impersonation is unavailable remain supportable through the pinned local process
token. Network-logon tokens are denied by the pipe ACL before this path.

Trusted callers are stored as canonical user SIDs in the machine-wide
`REG_MULTI_SZ` value
`HKLM\SOFTWARE\RunAsHelper\AllowedCallerSids`. Display names are UI-only and no
password or credential is stored. User type is validated when an account is
added through the tray; thereafter the canonical SID is authoritative even when
name lookup or a domain controller is unavailable. Authorization still requires
exact equality with the authenticated caller's `TokenUser`, so a group SID
manually inserted by an administrator cannot authorize its members. The list is
capped at 128 accounts. An empty or absent value preserves the old default
for local callers: only the installed elevated tray can launch while the CLI
gate is closed. MSI upgrades preserve this runtime policy. The normal MSI uninstall also
leaves it in place for a later reinstall; the repository's `uninstall.py`
cleanup tool deletes the entire `HKLM\Software\RunAsHelper` key.

Also out of scope:

- An administrator using the tool as designed to modify protected files,
  registry keys or services. That is the product.
- Privilege escalation that requires administrator rights to begin with.
- The self-signed *Serenity Software* certificate not being trusted on your
  machine. It is not meant to be. See the README.
- Reports generated by scanners with no demonstrated path to the outcome.
