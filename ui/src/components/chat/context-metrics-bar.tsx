import { Fragment, memo } from "react"
import {
  useChatStore,
  selectContextUsage,
  selectSessionCost,
} from "@/store/chat.store"
import {
  useConnectionStore,
  selectProviderContext,
  selectProviderModel,
} from "@/store/connection.store"
import { useT, useLocale } from "@/i18n"

// ---------------------------------------------------------------------------
// ContextMetricsBar — the opencode-style footer under the chat input:
//   "deepseek-v4-flash · 57.3K (45%) · $0.13"
//     1. active model id
//     2. last step's token footprint vs context window
//     3. session running spend (catalog-priced estimate)
// joined by middle dots. Parts appear only when known.
//
// Data flow: assistant.completed carries per-step usage (footprint updates
// live); run.completed carries session_cost + context_window; health seeds
// the model + window; session loads re-seed from the persisted last-
// assistant usage.
// ---------------------------------------------------------------------------

/** "146.4K" / "1.2M" (opencode Locale.number). */
function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`
  return String(n)
}

/** Small amounts deserve sub-cent precision; larger ones read like money. */
function formatCost(usd: number): string {
  if (usd <= 0) return "$0"
  if (usd < 0.01) return "<$0.01"
  if (usd < 1) return `$${usd.toFixed(3)}`
  return `$${usd.toFixed(2)}`
}

export const ContextMetricsBar = memo(function ContextMetricsBar(): React.JSX.Element | null {
  const t = useT()
  const locale = useLocale()
  const contextUsage = useChatStore(selectContextUsage)
  const sessionCost = useChatStore(selectSessionCost)
  const providerContext = useConnectionStore(selectProviderContext)
  const providerModel = useConnectionStore(selectProviderModel)

  const window =
    contextUsage?.window && contextUsage.window > 0
      ? contextUsage.window
      : providerContext ?? 0

  const tokens = contextUsage?.tokens ?? 0
  const hasUsage = tokens > 0
  const hasCost = sessionCost !== null && sessionCost > 0

  const pct =
    hasUsage && window > 0 ? Math.round((tokens / window) * 100) : null
  const nearLimit = pct !== null && pct >= 80

  const parts: { key: string; node: React.ReactNode }[] = []

  if (providerModel) {
    parts.push({
      key: "model",
      node: (
        <span className="min-w-0 truncate" title={t("messages.metrics.activeModel", { model: providerModel })}>
          {providerModel}
        </span>
      ),
    })
  }

  if (hasUsage) {
    parts.push({
      key: "context",
      node: (
        <span
          className={nearLimit ? "font-medium text-amber-600 dark:text-amber-500" : undefined}
          title={
            window > 0
              ? t("messages.metrics.contextOfWindow", {
                  tokens: tokens.toLocaleString(locale),
                  window: window.toLocaleString(locale),
                })
              : t("messages.metrics.context", { tokens: tokens.toLocaleString(locale) })
          }
        >
          {pct !== null ? `${formatTokens(tokens)} (${pct}%)` : formatTokens(tokens)}
        </span>
      ),
    })
  }

  if (hasCost) {
    parts.push({
      key: "cost",
      node: (
        <span title={t("messages.metrics.cost")}>
          {formatCost(sessionCost)}
        </span>
      ),
    })
  }

  if (parts.length === 0) return null

  return (
    <div className="flex items-center px-3 pb-1.5 pt-0 text-[0.625rem] leading-none text-muted-foreground">
      {parts.map((part, idx) => (
        <Fragment key={part.key}>
          {idx > 0 && (
            <span aria-hidden="true" className="mx-1.5 opacity-60">
              ·
            </span>
          )}
          {part.node}
        </Fragment>
      ))}
    </div>
  )
})
