import { memo } from "react"
import {
  BarChart2,
  FileSpreadsheet,
  Heading,
  ImageIcon,
  ListChecks,
  Palette,
  Presentation,
  SpellCheck,
  StickyNote,
  Table2,
  TrendingUp,
} from "lucide-react"
import { useT, type MessageKey } from "@/i18n"
import { useConnectionStore, selectHostApp } from "@/store/connection.store"

// ---------------------------------------------------------------------------
// Per-app suggestions aligned with each host's toolset
// ---------------------------------------------------------------------------

interface Suggestion {
  id: string
  textKey: MessageKey
  Icon: typeof Table2
}

interface HostCopy {
  subtitleKey: MessageKey
  suggestions: Suggestion[]
}

const EXCEL: HostCopy = {
  subtitleKey: "shell.emptySubtitle",
  suggestions: [
    { id: "suggest-pivot", textKey: "shell.suggestPivot", Icon: Table2 },
    { id: "suggest-chart", textKey: "shell.suggestChart", Icon: BarChart2 },
    { id: "suggest-analyze", textKey: "shell.suggestAnalyze", Icon: TrendingUp },
    { id: "suggest-formula", textKey: "shell.suggestFormula", Icon: FileSpreadsheet },
  ],
}

const HOST_COPY: Record<string, HostCopy> = {
  excel: EXCEL,
  word: {
    subtitleKey: "shell.emptySubtitle.word",
    suggestions: [
      { id: "suggest-summary", textKey: "shell.suggestWordSummary", Icon: ListChecks },
      { id: "suggest-headings", textKey: "shell.suggestWordHeadings", Icon: Heading },
      { id: "suggest-table", textKey: "shell.suggestWordTable", Icon: Table2 },
      { id: "suggest-proofread", textKey: "shell.suggestWordProofread", Icon: SpellCheck },
    ],
  },
  powerpoint: {
    subtitleKey: "shell.emptySubtitle.powerpoint",
    suggestions: [
      { id: "suggest-deck", textKey: "shell.suggestSlidesDeck", Icon: Presentation },
      { id: "suggest-theme", textKey: "shell.suggestSlidesTheme", Icon: Palette },
      { id: "suggest-image", textKey: "shell.suggestSlidesImage", Icon: ImageIcon },
      { id: "suggest-notes", textKey: "shell.suggestSlidesNotes", Icon: StickyNote },
    ],
  },
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

interface ChatEmptyStateProps {
  onSelectSuggestion: (text: string) => void
}

export const ChatEmptyState = memo(function ChatEmptyState({
  onSelectSuggestion,
}: ChatEmptyStateProps): React.JSX.Element {
  const t = useT()
  const hostApp = useConnectionStore(selectHostApp)
  const { subtitleKey, suggestions } = HOST_COPY[hostApp ?? ""] ?? EXCEL
  return (
    <div
      className="flex h-full min-h-[280px] flex-col items-center justify-center gap-2 p-8 text-center"
      role="main"
      aria-label={t("shell.startConversation")}
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
        {t("shell.emptyTitle")}
      </h2>
      <p className="m-0 text-xs text-muted-foreground">
        {t(subtitleKey)}
      </p>

      {/* Suggestion chips */}
      <div className="mt-2 flex w-full max-w-[300px] flex-col gap-1.5">
        {suggestions.map(({ id, textKey, Icon }) => {
          const text = t(textKey)
          return (
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
          )
        })}
      </div>
    </div>
  )
})
