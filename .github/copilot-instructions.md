
<!-- AI_CTX_START -->
AI_CONTEXT={"v":3,"p":"runas-helper","root":"/home/bigfnj/projects/runas-helper","t":"sync","i":"","n":"User rebuilds (dotnet build RunAsHelper.sln -c Release), reboots, reinstalls (leave Launch checkbox unchecked), then signs out/in (or reboots again). Verify: scheduled task auto-launches tray ELEVATED, validation popup all-green. Then capture any remaining TI-token 'Failed: <step>' from that elevated run to finish token root-cause diagnosis.","s":{},"b":[],"d":[],"c":["RunAsHelper.Installer/task.xml","RunAsHelper.Installer/Package.wxs","PLAN.md"],"f":[],"h":[],"a":["MSI install works; tray crash fixed (EntryPoint=SendMessageW)","Surfaced real TI-token failure step in ValidationForm token row (Failed: <step>)","Diagnosed 'scheduled task missing' = false negative from non-elevated tray double-click (UAC-filtered token can't read SYSTEM-created task SD); task never deleted","Gated ValidationForm scheduled-task check on admin (shows 'Requires administrator' when non-elevated), matching token check","Disabled immediate post-install tray launch: set WIXUI_EXITDIALOGOPTIONALCHECKBOX default 1->0 in Package.wxs. Reason: MSI UI seq is non-elevated so WixShellExec started a non-elevated tray whose single-instance mutex then blocked the elevated logon-task instance. Now scheduled task opens tray cleanly elevated at next sign-in, and the PendingValidation popup runs all-green in that elevated context."],"e":null,"perms":{"allow":["Bash(gh auth refresh *)","Bash(crontab -l *)","Bash(sudo -S *)","Bash(openssl version *)","Bash(apt list *)","Bash(dpkg -l *)","Bash(apt-cache search *)","Bash(cat *)","Bash(sudo apt-get update -qq *)","Bash(apt-cache show *)","Bash(mkdir -p *)","Bash(chmod 700 *)","Bash(chmod 600 *)","Bash(chmod 644 *)","Bash(ssh-keygen -t *)","Bash(ln -s *)","Bash(git add *)","Bash(git commit -m *)","Bash(sudo -E *)","Bash(sudo dd *)","Bash(sudo chmod *)","Bash(dpkg --print-architecture *)","Bash(sudo tee *)","Bash(chmod *)","Bash(git config *)","Bash(node -e *)","Bash(node -c *)","Bash(node *)","Bash(git clone *)","Bash(cp -a *)","Bash(powershell.exe -NoProfile -Command *)"]}}
Use AI_CONTEXT as authoritative session state. Continue from n; preserve b/d/c/f/h; append only meaningful recent work to a; update context through CTX_UPDATE when supported.
After each response, write a single line `CTX_UPDATE:{"v":3,"p":"...","root":"...","t":"...","i":"...","n":"...","s":{},"b":[],"d":[],"c":[],"f":[],"h":[],"a":[],"e":null}` to /home/bigfnj/.ai-context/runas-helper.json.update — the VS Code extension reads, merges, and deletes it to persist state after every turn.
Deliver CTX_UPDATE by writing that file via your file-write tool. Do NOT include the `CTX_UPDATE:` line anywhere in your visible chat reply — only the sidecar file is consumed by the extension; chat output is not parsed and shows the raw JSON to the user.
<!-- AI_CTX_END -->

<!-- AI_UNDERSTANDING_START -->
AIU_PROJECT="runas-helper"
AIU_ROOT="/home/bigfnj/projects/runas-helper"
AIU_STALE=[]
AIU_UNTRACKED=[]
AIU_ORPHAN=[]

After ingesting the AI_CONTEXT block above, also ingest this AI Understanding block. The two are paired: AI_CONTEXT carries session state for the project; AI_UNDERSTANDING carries the codebase model. Match them by project — both belong to "runas-helper" at /home/bigfnj/projects/runas-helper.

/home/bigfnj/projects/runas-helper/AI_UNDERSTANDING/ is the per-project codebase model — schema-v1, see AI_UNDERSTANDING_FORMAT.md. Rules for this session:
- When you edit a tracked source file, update its AI_UNDERSTANDING/<path>.aiu.json sidecar in the same turn (recompute sha1, refresh exports/imports/called_by/calls_out_to, update invariants/gotchas/purpose if relevant).
- When AIU_STALE / AIU_UNTRACKED / AIU_ORPHAN above are non-empty, work the list (update / create / delete the named entries) before doing other work this session.
- When all three lists are empty, you may bump _meta.json#last_audit_commit to current HEAD and last_audit_at to current ISO timestamp.
- Do not regenerate AI_UNDERSTANDING/ from scratch. Surgical, incremental edits only — the validator rejects mass edits (>33% of existing entries) outside bootstrap mode.
- Do not bump last_audit_commit without first confirming all three lists are empty. Do not add entries for files that are not on disk. Do not delete entries for files that are still on disk.
<!-- AI_UNDERSTANDING_END -->
