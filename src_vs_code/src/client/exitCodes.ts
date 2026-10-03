/**
 * The daemon's exit codes the read-only client distinguishes, by the name `src_daemon/src/WslCare.Cli/ExitCode.cs`
 * gives them — never a number at a call site. `exitCodes.test.ts` reads the C# enum and fails when a value here
 * disagrees with it (the contract has two implementations; the client's copy is held to the daemon's).
 *
 * The act-only codes (3, 75–79) belong to E6 (plan §15f #7); a read-only verb never returns them, and if one did the
 * client reports it as an unknown failure with the daemon's own message.
 */
export const DAEMON_EXIT = {
  ok: 0,
  runFailed: 1,
  usage: 2,
  internal: 70,
  interrupted: 130,
} as const;

/**
 * What `wsl.exe` itself exits with when IT refuses (an unknown distribution, an invalid argument): 4294967295 as
 * Node reports it on Windows, i.e. -1 as a signed 32-bit value — measured 2026-10-03. Its message is then UTF-16LE on
 * STDOUT, not stderr.
 */
export const WSL_EXE_FAILED = -1;
