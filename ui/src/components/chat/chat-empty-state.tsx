import { memo } from "react"
import { BarChart2, FileSpreadsheet, Table2, TrendingUp } from "lucide-react"

// ---------------------------------------------------------------------------
// Excel-specific suggestions aligned with the add-in's domain
// ---------------------------------------------------------------------------

interface Suggestion {
  id: string
  text: string
  Icon: typeof Table2
}

const SUGGESTIONS: Suggestion[] = [
  {
    id: "suggest-pivot",
    text: "Create a pivot table from the data in Sheet1",
    Icon: Table2,
  },
  {
    id: "suggest-chart",
    text: "Generate a bar chart from columns A and B",
    Icon: BarChart2,
  },
  {
    id: "suggest-analyze",
    text: "Analyze this spreadsheet and show key insights",
    Icon: TrendingUp,
  },
  {
    id: "suggest-formula",
    text: "Write a VLOOKUP formula to match customer IDs",
    Icon: FileSpreadsheet,
  },
]

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

interface ChatEmptyStateProps {
  onSelectSuggestion: (text: string) => void
}

export const ChatEmptyState = memo(function ChatEmptyState({
  onSelectSuggestion,
}: ChatEmptyStateProps): React.JSX.Element {
  return (
    <div
      className="flex h-full min-h-[280px] flex-col items-center justify-center gap-2 p-8 text-center"
      role="main"
      aria-label="Start a conversation"
    >
      {/* Logo mark */}
      <div
        className="relative mb-1 size-12"
        aria-hidden="true"
      >
        <div className="absolute inset-0 rounded-full bg-primary/15" />
        <span className="absolute inset-0 flex items-center justify-center text-2xl font-extrabold tracking-tighter text-primary">
          H
        </span>
      </div>

      {/* Headline */}
      <h2 className="m-0 text-[0.9375rem] font-semibold text-foreground">
        How can I help you today?
      </h2>
      <p className="m-0 text-xs text-muted-foreground">
        I can read, analyze and modify your spreadsheet — just ask.
      </p>

      {/* Suggestion chips */}
      <div className="mt-2 flex w-full max-w-[300px] flex-col gap-1.5">
        {SUGGESTIONS.map(({ id, text, Icon }) => (
          <button
            key={id}
            id={id}
            type="button"
            className="flex items-start gap-2 rounded-lg border border-border bg-background p-2 text-left text-[0.6875rem] leading-snug text-foreground transition-colors hover:border-primary/50 hover:bg-primary/5"
            onClick={() => onSelectSuggestion(text)}
            title={text}
          >
            <Icon
              size={14}
              className="mt-0.5 shrink-0 text-primary"
              aria-hidden="true"
            />
            <span className="min-w-0 flex-1">{text}</span>
          </button>
        ))}
      </div>
    </div>
  )
})
