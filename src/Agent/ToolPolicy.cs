using System.Collections.Generic;

namespace AgentOffice.Agent
{
/// <summary>Classifies tools by whether they modify the open document.</summary>
    internal static class ToolPolicy
    {
        private static readonly HashSet<string> ReadOnly = new HashSet<string>
        {
            "ping", "evaluate", "calculate", "capture_range",
            "read_range", "read_selection", "read_sheet_names", "read_table",
            "read_workbook_context", "read_workbook_snapshot",
            "read_document_snapshot", "read_text",
            "read_presentation_snapshot", "read_slide", "list_themes",
            "export_pdf", "generate_image",
        };

/// <summary>True unless the tool is known to leave the document untouched; unknown tools count as changes.</summary>
        internal static bool ChangesDocument(string toolName)
        {
            return string.IsNullOrEmpty(toolName) || !ReadOnly.Contains(toolName);
        }
    }
}
