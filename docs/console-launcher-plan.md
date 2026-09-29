# Plan: console launcher so CLI callers don't need `| Out-String`

Status: **not started** — notes written 2026-09-29 to pick up on another machine.

## The problem

`RunAsHelper.exe` is one binary for both the tray app and the CLI, built as
`<OutputType>WinExe</OutputType>` (`RunAsHelper/RunAsHelper.csproj:3`). A shell never
waits for a GUI-subsystem process, so a bare call from PowerShell:

```powershell
& "C:\Program Files\RunAsHelper\RunAsHelper.exe" /capture /as:system powershell.exe -NoProfile -File x.ps1
```

returns immediately with **no output and an empty `$LASTEXITCODE`**, even with
`/capture`. The service still receives the request (Application log events 1001/1002
fire) and the child still runs. It just looks like the gate is closed or the child
did nothing.

The workaround is caller-side: pipe the call (`... | Out-String`), which makes
PowerShell wait and read stdout. Then the service log, the child's output and the exit
code all come back. The code already says so in the comment above `ShowConsole()`
(`RunAsHelper/Program.cs:289`): *"the shell does not wait for a WinExe… piping fixes
the ordering, caller-side."* `AttachConsole` can make text visible, but nothing inside
a WinExe can make its parent shell wait; the PE subsystem flag decides that before
any of our code runs.

**How it was found (2026-09-29):** an elevated cleanup script failed on its first line
(`$PSScriptRoot` empty under Windows PowerShell 5.1, see below). The failure was
completely invisible until the call was piped. The first two attempts looked like
successes that changed nothing.

## Proposed fix: a `RunAsHelper.com` companion (the devenv.com pattern)

Ship a small **console-subsystem** launcher named `RunAsHelper.com` next to
`RunAsHelper.exe`, the same trick Visual Studio uses with `devenv.com` / `devenv.exe`.

- `PATHEXT` is `.COM;.EXE;...` on stock Windows, so a bare `RunAsHelper` from cmd or
  PowerShell resolves to the `.com` first. The shell waits for it like any console
  tool: output streams, the exit code lands in `$LASTEXITCODE` / `%ERRORLEVEL%`.
- The tray, the startup shortcut, Explorer and anything naming `RunAsHelper.exe`
  explicitly keep getting the GUI binary, so they get no console window.
- The Windows loader runs a PE image regardless of the `.com` extension.

### What the `.com` does (thin shim, no duplicated CLI logic)

1. `CreateProcess` `RunAsHelper.exe` (same folder as itself) with the **same command
   line**, `bInheritHandles = TRUE` and `STARTF_USESTDHANDLES` set to its own
   stdin/stdout/stderr.
2. `WaitForSingleObject` on the child, then `GetExitCodeProcess`, and exit with that
   code.

The `.exe` should need no change. `NativeMethods.HasUsableStdOut()`
(`RunAsHelper/Core/NativeMethods.cs:66`) treats any handle whose `GetFileType` is not
`FILE_TYPE_UNKNOWN` as usable. An inherited console handle is `FILE_TYPE_CHAR` and a
pipe is `FILE_TYPE_PIPE`, so `ShowConsole()` skips `AttachConsole` and writes straight
to what the shim passed in. **Verify this on a real console**, not just a pipe; it's
the one assumption the design rests on.

### Things to decide or check while building it

- **Ctrl+C:** the child shares the shim's console, so it gets the event too. The shim
  should ignore Ctrl+C (`SetConsoleCtrlHandler(NULL, TRUE)`) and keep waiting for the
  child, so exit-code relay still works when the user interrupts.
- **`/timeout:N`:** the `.exe` already enforces it; the shim just waits on the child. Don't
  add a second timeout.
- **No arguments / double-click:** if the `.com` is run with no args, it could just
  launch the tray `.exe` and exit, so it's harmless if someone opens it.
- **Language:** a tiny C# console project (`<OutputType>Exe</OutputType>`), renamed to
  `.com` after build (`<TargetExt>`/post-build copy), is simplest and matches the repo.
  If startup cost matters, NativeAOT keeps it small and fast.

### Rejected alternative

**Switch `RunAsHelper.exe` itself to `<OutputType>Exe</OutputType>`** and `FreeConsole()`
in tray mode. That flashes a console window every time the tray starts at login, from
the Startup shortcut or from Explorer.

## Also do in the same change

- **Installer: add `C:\Program Files\RunAsHelper` to the machine PATH.** It isn't on
  it today. Then the documented call becomes `RunAsHelper /capture /as:system ...` by
  bare name, which also lets Claude Code wildcard the permission approval (a quoted
  absolute path can't be wildcarded).
- **Signing:** `signing/Build-Signed.ps1` must sign the `.com` too, or endpoint
  protection may flag an unsigned binary launching an elevation client.
- **Release:** add the `.com` to `.github/workflows/release.yml` artifacts and to
  `RunAsHelper.Installer`.
- **README:** the CLI examples around `README.md:317-323` show bare
  `RunAsHelper.exe ...` calls with no pipe. That's exactly the silent form. Update them
  to the bare `RunAsHelper` name once the `.com` ships, and note `| Out-String` for
  anyone on an older version or calling the `.exe` by full path.
- **Help text:** `RunAsHelper/HelpText.cs` might add a line on `.com` vs `.exe`.

## Related findings from the same session (no code change planned)

- **`.ps1` auto-hosting always uses Windows PowerShell 5.1**
  (`RunAsHelper.Service/Core/ElevationLauncher.cs:1052`, command line built at `:1156`).
  Keep that default: `powershell.exe` exists on every Windows box and `pwsh` doesn't, and
  flipping it would silently change how existing saved `.ps1` entries run. `pwsh.exe` can
  already be passed explicitly (both PowerShell folders are on the machine PATH, and
  bare names resolve via PATH since 1.4.2). **Optional opt-in** if wanted later: when
  auto-hosting a `.ps1` whose header contains `#Requires -Version 7`, launch it with
  `pwsh.exe`. No behavior change for scripts without the line, and 5.1 already fails
  such scripts with a clear version error.
- **Caller gotchas under the 5.1 host** (worth a README line): `$PSScriptRoot` is empty
  inside `param()` default values, so resolve paths after the param block from
  `$MyInvocation.MyCommand.Path`. PS7-only syntax (`&&`, `||`, `??`, ternary) won't
  parse. `Set-Content` defaults to ANSI. A script that passes a dry run in a pwsh 7
  shell proves nothing about the gate.
- **The gate itself behaved correctly throughout.** Every failure on 2026-09-29 was
  caller-side: the missing pipe and the script bug.

## Caller-side notes already recorded

The work machine's global `~/.claude/CLAUDE.md` (RunAS Helper section) was updated
2026-09-29 with the `| Out-String` requirement and the "gate scripts run under 5.1"
rule. Those edits are local to that machine and not in this repo. Once the `.com`
ships, update them to the bare `RunAsHelper` form.
