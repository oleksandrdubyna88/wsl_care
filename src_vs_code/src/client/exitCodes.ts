/**
 * The daemon's exit codes the read-only client distinguishes, by the name `src_daemon/src/WslCare.Cli/ExitCode.cs`
 * gives them — never a number at a call site. `exitCodes.test.ts` reads the C# enum and fails when a value here
 * disagrees with it (the contract has two implementations; the client's copy is held to the daemon's).
 *
 * Since E6.S2 every code of `contracts/exit-codes.json` is named here (that file is written from the same enum by the
 * daemon's `ContractFilesTests`; `exitCodes.test.ts` holds the two EQUAL, both ways): the read-only verbs return the
 * first five, the root paths (`root/`) the rest — a read-only verb answering an act code still reads as an unknown failure.
 */
export const DAEMON_EXIT = {
  ok: 0,
  runFailed: 1,
  usage: 2,
  actionFailed: 3,
  recordsUnreadable: 4,
  detachUnavailable: 69,
  internal: 70,
  detachStartFailed: 71,
  queueFull: 73,
  busy: 75,
  wedged: 76,
  needsRoot: 77,
  observeOnly: 78,
  stateUnreadable: 79,
  requestGone: 80,
  /** E7 (#17): a verb that must not run as uid 0 was run as root — the distribution's default user is root. */
  notAsRoot: 81,
  /** The daemon's retro round over PR #11: `act --request` met a request it cannot use — recorded refused and removed. */
  requestUnusable: 82,
  /** E14 S6: `busy` only — the machine is too busy to START heavy work now (advice; not `busy`, 75, a held run lock). */
  machineBusy: 83,
  interrupted: 130,
} as const;

/**
 * What `wsl.exe` itself exits with when IT refuses (an unknown distribution, an invalid argument): 4294967295 as
 * Node reports it on Windows, i.e. -1 as a signed 32-bit value — measured 2026-10-03. Its message is then UTF-16LE on
 * STDOUT, not stderr.
 */
export const WSL_EXE_FAILED = -1;
