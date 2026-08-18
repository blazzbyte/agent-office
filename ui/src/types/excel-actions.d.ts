/**
 * Excel Action and Execution Types
 */

export type ExcelActionType =
  // Read operations
  | "read_range"
  | "read_sheet_names"
  | "read_table"
  | "read_selection"

  // Write operations
  | "write_range"
  | "write_formula"

  // Worksheet operations
  | "create_sheet"
  | "rename_sheet"
  | "delete_sheet"

  // Formatting
  | "format_range"
  | "auto_fit"

  // Tables
  | "create_table"
  | "add_table_row"
  | "sort_table"

  // Charts
  | "create_chart"
  | "configure_chart"

  // Utility
  | "calculate"

  // Pro actions (executed natively via COM in the host VSTO add-in)
  | "ping"
  | "undo"
  | "evaluate"
  | "insert_equation"
  | "execute_mso"
  | "goal_seek"
  | "export_pdf"
  | "create_pivot_table"
  | "capture_range"
  | "add_power_query"
  | "configure_chart_pro"
  | "create_slicer"
  | "protect_sheet"
  | "protect_workbook"
  | "page_setup"

  // Internal host actions (not documented in the agent prompt — used by the UI
  // itself for workbook context, like `ping`)
  | "read_workbook_context"
  | "read_workbook_snapshot"

export interface ExcelAction {
  id: string
  action: ExcelActionType
  params: Record<string, unknown>
  label?: string
}

export interface StepResult {
  id: string
  status: "success" | "error"
  summary: string
  data?: unknown
}
