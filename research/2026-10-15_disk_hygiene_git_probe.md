# Git for Windows, read-only — measured 2026-10-15

> Measured 2026-10-15 against `git version 2.55.0.windows.3`, in throw-away `testsToDelete-` repositories only; read-only
> commands; nothing real was touched. What [PLAN_disk_hygiene_windows_E19.md](../todo/PLAN_disk_hygiene_windows_E19.md) story
> E19.S4 builds on, and where it overrides the command shapes that plan was written with.

## What git does

- `check-ignore -z` WITHOUT `--stdin` exits 128: "-z only makes sense with --stdin". So `check-ignore` runs in ARGV form, without `-z`.
- `check-ignore` refuses `--literal-pathspecs` and `GIT_LITERAL_PATHSPECS=1` (exit 128, "pathspec magic not supported by this
  command: 'literal'"); `ls-files` accepts both. So the `RelativeName` slot, not git, makes a name a literal: it refuses glob
  characters, `:`, a leading `-`, `..`, a rooted path and a backslash.
- Argv form, no `-z`: `check-ignore -v -n` prints one line per path, `<source>:<line>:<pattern>\t<path>`. A path no rule ignores — and a
  path whose LAST deciding rule is a negation — prints `::\t<path>`. Exit 0 = at least one path ignored, 1 = none, 128 = fatal. So a
  batch is judged per line, never by the exit code, and a negation reads as "not ignored".
- `-c core.quotePath=false` keeps a non-ASCII path from being octal-escaped and quoted. The runner must also decode git's output as
  UTF-8 (`CommandRequest.Utf8Output`): .NET decodes a redirected stream with the console's OEM code page, and a Cyrillic folder name then
  no longer matches the name asked about (seen in `A_non_ascii_path_is_parsed`).
- `rev-parse --show-toplevel` prints forward slashes and LF (compare after normalising, case-insensitively). Under a broken `.git` file
  it exits 128, "fatal: not a git repository: (NULL)".
- `ls-files -z --cached -- bin/` prints cwd-relative paths, NUL-terminated.
- A clean environment works with ONLY `SystemRoot`, `WINDIR`, `TEMP`, `TMP`, `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0`,
  `GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL=NUL`, `GIT_CEILING_DIRECTORIES=<root>`, plus `-c core.excludesFile=` in argv: no warnings,
  identical answers. Without isolation the user's `core.excludesFile` leaks into `check-ignore`; `GIT_CONFIG_GLOBAL=NUL` alone does NOT
  stop the XDG `git/ignore` file — only an environment without `HOME`, `USERPROFILE` and `XDG_CONFIG_HOME` does.
- An orphan (a folder whose `.git` file points at an admin folder that still exists): `git --git-dir=<common> --work-tree=. check-ignore
  --no-index -v -n -- bin obj`, run from the orphan, works (exit 0) and reads the orphan's own `.gitignore`.
- 64 names of 120 characters (about 7.9 KB of argv) ran. The batch is bounded by count (`worktrees.gitNamesPerCall`, 64) AND by the
  encoded command line (`worktrees.gitCommandLineMaxChars`, 7000); a name that cannot fit alone is retained, "too long for one git call".
- `git status` refreshes a stale index and rewrites it; M2 measured the same sweep over 321 worktrees making 9 of them look active for
  days. `--no-optional-locks` plus the three read-only questions leave the index's bytes and mtime unchanged
  (`The_scan_never_changes_the_index_time`, with a `git status` control that does change them).

## Measured 2026-10-21 (after the security review of PR #159), same git, throw-away `testsToDelete-` repositories

- **Case.** Index `Bin/x` (force-added), folder renamed to `bin` on disk, `.gitignore` `bin/`, `core.ignorecase=true`: `check-ignore -v -n -- bin` says
  ignored (the ignore matcher honours `core.ignorecase`); `ls-files --cached -- bin` and `--literal-pathspecs ls-files --cached -- bin` print NOTHING (the
  pathspec matcher does not); `ls-files --cached -- ':(icase)bin'` and `':(literal,icase)bin'` print `Bin/x`, with or without a trailing `/`. With
  `GIT_LITERAL_PATHSPECS=1` the same `:(icase)` pathspec prints nothing (no magic is parsed). So the question is asked per name as `:(literal,icase)<name>`
  and without `--literal-pathspecs`; `check-ignore` alone would have counted the tracked file reclaimable.
- **Submodule.** Index gitlink `sub` (`update-index --add --cacheinfo 160000,<sha>,sub`), folder `sub/bin` on disk: `ls-files --cached -- ':(literal,icase)sub/bin'`
  prints nothing, `... -- ':(literal,icase)sub'` prints `sub`, and `check-ignore -v -n -- sub/bin` exits 128, "fatal: Pathspec 'sub/bin' is in submodule 'sub'". So git
  itself refuses the candidate (retained by name); a tracked gitlink listed ABOVE a candidate is also treated as tracked.
- **Last tab.** `check-ignore -v` prints `<source>:<line>:<pattern><TAB><path>`; a pattern may hold a tab and a colon (`ob[:<TAB>-k]/` matches `obj`), a path cannot
  (a control character is no Windows file name): the path is what follows the LAST tab.
- **A linked worktree needs no commit.** `git worktree add --orphan -b feature ../wt1` makes a linked worktree on an unborn branch: a `.git` file, an admin folder whose
  `gitdir` points back, and no identity needed anywhere.
- **Cost of judging `git.exe` at every call.** Reading the security descriptors of the install (file, `cmd`, `mingw64\bin` and its `git.exe`, and every ancestor to `C:\`)
  costs about 12 ms a resolve on this machine (50 resolves in 601 ms; a second run 465 ms, 9 ms a call), against a 5 ms / 1 s budget. The product therefore keeps the
  judgement of a passed candidate for the life of the process and re-checks the identity (path, length, creation and last-write time) of every program file at each call.
- **A timeout kills the tree.** A fake `git.exe` that starts a child and hangs is cut at `worktrees.gitTimeoutSeconds` by the shared launcher
  (`Process.Kill(entireProcessTree: true)`); the child is gone when the call returns (`GitTimeoutTests`).

## Consequences recorded in the code

`Core/Worktrees/GitCommands.cs` (the argv), `GitQuestions.cs` (the three questions and their parse), `GitEnvironment` (the clean
environment), `Processes/Policy/ReadCommandTemplates.cs` (the four templates), `Processes/GitExecutable.cs` (which `git.exe`).
