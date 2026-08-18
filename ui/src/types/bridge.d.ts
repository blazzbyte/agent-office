/**
 * Agent Office native bridge (in-proc VSTO executor) types
 */

/** Shape returned by `GET /bridge/v1/health` */
export interface BridgeHealth {
  name: string
  version: string
  protocol: number
  /** e.g. "EXCEL.EXE:12345" — identifies the hosting Excel process */
  instance: string
  /** Excel version reported by the bridge (e.g. "16.0") */
  excel: string
  /** Actions the bridge can execute (capability advertisement) */
  actions: string[]
}
