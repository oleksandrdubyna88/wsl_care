/**
 * Whom a task of this extension runs as — defined ONCE for the scheduler layer (WR-E4, the coai code round 1c023671, finding 1): an interactive token
 * (LogonType 3, TASK_LOGON_INTERACTIVE_TOKEN) at the least privilege (RunLevel 0, TASK_RUNLEVEL_LUA), as the reader's own account. The numbers are the
 * ones SUMMARY_FUNCTION prints (pinned by summaryOfXml.ts). The run-now script builds its in-script check from them and the flow builds the principal
 * line it expects of the READ from them, so the two cannot drift.
 */
export const PRINCIPAL_POLICY = { logonType: 3, runLevel: 0 } as const;

/** The first line of the canonical summary of a task registered as this extension registers it, for the reader whose SID is `user`. */
export function principalLine(user: string): string {
  return `principal=${user} logon=${PRINCIPAL_POLICY.logonType} level=${PRINCIPAL_POLICY.runLevel}`;
}
