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

## Consequences recorded in the code

`Core/Worktrees/GitCommands.cs` (the argv), `GitQuestions.cs` (the three questions and their parse), `GitEnvironment` (the clean
environment), `Processes/Policy/ReadCommandTemplates.cs` (the four templates), `Processes/GitExecutable.cs` (which `git.exe`).
