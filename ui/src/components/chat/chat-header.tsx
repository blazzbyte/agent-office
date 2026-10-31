import { memo } from "react"
import { History, PenSquare, Zap, Settings } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Avatar, AvatarFallback } from "@/components/ui/avatar"
import { cn } from "@/lib/utils"
import type { ConnectionStatus } from "@/store/connection.store"
import type { BridgeStatus } from "@/store/bridge.store"
import { useT } from "@/i18n"

// ---------------------------------------------------------------------------
// Status indicator config — narrow taskpane header: status is conveyed with a
// colored dot + tooltip, never with text labels (they don't fit at ~320px).
// ---------------------------------------------------------------------------

const DOT_CONFIG: Record<
  ConnectionStatus,
  { dotClass: string; pulse: boolean }
> = {
  idle:      { dotClass: "bg-muted-foreground", pulse: true },
  checking:  { dotClass: "bg-muted-foreground", pulse: true },
  healthy:   { dotClass: "bg-green-500",        pulse: false },
  unhealthy: { dotClass: "bg-destructive",      pulse: false },
}

/** Host app ids → display labels (tooltips only — no room for text badges). */
const HOST_LABELS: Record<string, string> = {
  excel: "Excel",
  word: "Word",
  powerpoint: "PowerPoint",
}

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

interface ChatHeaderProps {
  status: ConnectionStatus
  gatewayVersion?: string | null
  /** Office app hosting the add-in ("excel" | "word" | "powerpoint") */
  hostApp?: string | null
  /** Host-side provider readiness (apiKey + model present) */
  providerConfigured?: boolean | null
  /** Active model id reported by the host */
  providerModel?: string | null
  /** Native executor (in-proc VSTO bridge) status */
  bridgeStatus?: BridgeStatus
  bridgeVersion?: string | null
  /** Open the session history sheet */
  onHistoryClick?: () => void
  /** Start a new chat session */
  onNewChatClick?: () => void
  /** Open the connection settings sheet */
  onSettingsClick?: () => void
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export const ChatHeader = memo(function ChatHeader({
  status,
  gatewayVersion,
  hostApp,
  providerConfigured,
  providerModel,
  bridgeStatus,
  bridgeVersion,
  onHistoryClick,
  onNewChatClick,
  onSettingsClick,
}: ChatHeaderProps): React.JSX.Element {
  const t = useT()
  const { dotClass, pulse } = DOT_CONFIG[status]
  // Healthy agent but provider not configured → amber dot (action needed).
  const effectiveDotClass =
    status === "healthy" && providerConfigured === false
      ? "bg-amber-500"
      : dotClass

  const hostLabel = HOST_LABELS[hostApp ?? ""] ?? null

  const connectionTitle =
    status === "healthy"
      ? providerConfigured === false
        ? t("shell.providerNotConfigured")
        : t("shell.connected", {
            model: providerModel ?? t("shell.modelReady"),
            version: gatewayVersion ?? "?",
            host: hostLabel ? ` · ${hostLabel}` : "",
          })
      : status === "unhealthy"
        ? t("shell.offline")
        : t("shell.checkingAgent")

  const bridgeTitle =
    bridgeStatus === "connected"
      ? `Agent Office v${bridgeVersion ?? "?"}`
      : bridgeStatus === "unavailable"
        ? t("shell.engineOffline")
        : t("shell.checkingEngine")

  return (
    <header
      className="flex shrink-0 items-center justify-between gap-2 border-b bg-background px-3.5 py-2"
      role="banner"
    >
      {/* Brand */}
      <div className="flex min-w-0 items-center gap-2">
        {onHistoryClick && (
          <Button
            type="button"
            id="chat-history-btn"
            variant="ghost"
            size="icon-sm"
            onClick={onHistoryClick}
            aria-label={t("shell.openHistory")}
            title={t("shell.conversations")}
          >
            <History size={14} />
          </Button>
        )}
        <Avatar className="size-7 text-[0.7rem]" aria-hidden="true">
          <AvatarFallback className="bg-primary text-primary-foreground font-bold">
            EA
          </AvatarFallback>
        </Avatar>
        <h1 className="truncate text-[0.8125rem] font-semibold leading-tight">
          Agent Office
        </h1>
      </div>

      {/* Right side: status indicators + actions */}
      <div className="flex shrink-0 items-center gap-0.5">
        {/* Native executor status — passive indicator */}
        {bridgeStatus != null && (
          <span
            className="flex size-7 cursor-help items-center justify-center"
            role="status"
            aria-label={bridgeTitle}
            title={bridgeTitle}
          >
            <Zap
              size={14}
              aria-hidden="true"
              className={cn(
                bridgeStatus === "connected"
                  ? "fill-primary text-primary"
                  : "text-muted-foreground",
              )}
            />
          </span>
        )}

        {onSettingsClick && (
          <Button
            type="button"
            id="chat-settings-btn"
            variant="ghost"
            size="icon-sm"
            onClick={onSettingsClick}
            aria-label={connectionTitle}
            title={connectionTitle}
            role="status"
          >
            <span className="relative flex items-center justify-center">
              <Settings size={14} aria-hidden="true" />
              <span
                className={cn(
                  "absolute -right-0.5 -top-0.5 size-1.5 rounded-full",
                  effectiveDotClass,
                  pulse && "animate-pulse",
                )}
                aria-hidden="true"
              />
            </span>
          </Button>
        )}

        {onNewChatClick && (
          <Button
            type="button"
            id="chat-new-btn"
            variant="ghost"
            size="icon-sm"
            onClick={onNewChatClick}
            aria-label={t("shell.newChat")}
            title={t("shell.newChat")}
          >
            <PenSquare size={14} />
          </Button>
        )}
      </div>
    </header>
  )
})
