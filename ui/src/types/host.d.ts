
export interface agentOfficeHostInfo {
  /** Name of the workbook this taskpane instance belongs to (e.g. "Sales.xlsx"). */
  workbook?: string | null
  /** Host add-in version, for diagnostics. */
  version?: string
}

declare global {
  interface Window {
    __agentOfficeHost?: agentOfficeHostInfo
  }
}

export {}
