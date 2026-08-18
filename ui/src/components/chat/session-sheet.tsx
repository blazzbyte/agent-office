import { memo, useCallback, useEffect, useRef, useState } from "react"
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import { MessageSquarePlus, Trash2, MessageSquare, Clock, X, Check } from "lucide-react"
import { listSessions, deleteSession } from "@/services/session.service"
import type { AgentSession } from "@/types/session"
import { cn } from "@/lib/utils"

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function formatSessionDate(dateStr?: string): string {
  if (!dateStr) return ""
  try {
    const date = new Date(dateStr)
    const now = new Date()
    const diff = now.getTime() - date.getTime()
    const days = Math.floor(diff / 86_400_000)
    if (days === 0) return "Today"
    if (days === 1) return "Yesterday"
    if (days < 7) return `${days} days ago`
    return date.toLocaleDateString(undefined, { month: "short", day: "numeric" })
  } catch {
    return ""
  }
}

function getSessionLabel(session: AgentSession): string {
  if (session.title && session.title !== "Excel Chat") return session.title
  if (session.preview) {
    const cleanPreview = session.preview
      .replace(/\[EXCEL_CONTEXT\][\s\S]*?(?:\[\/EXCEL_CONTEXT\]|$)/s, "")
      .replace(/\[REMINDER\][\s\S]*?(?:\[\/REMINDER\]|$)/s, "")
      .trim()
    return cleanPreview ? cleanPreview.slice(0, 50) : "Untitled conversation"
  }
  return "Untitled conversation"
}

// ---------------------------------------------------------------------------
// Session row
// ---------------------------------------------------------------------------

interface SessionRowProps {
  session: AgentSession
  isActive: boolean
  onSelect: (id: string) => void
  onDelete: (id: string) => void
}

const SessionRow = memo(function SessionRow({
  session,
  isActive,
  onSelect,
  onDelete,
}: SessionRowProps): React.JSX.Element {
  const [confirming, setConfirming] = useState(false)
  const label = getSessionLabel(session)
  const date = formatSessionDate(session.last_active ?? session.started_at)

  return (
    <div
      className={cn(
        "group/session relative flex items-center rounded-md border border-transparent transition-colors",
        isActive
          ? "bg-primary/10 border-primary/50"
          : "hover:bg-muted",
      )}
      role="listitem"
      onMouseLeave={() => setConfirming(false)}
    >
      <button
        type="button"
        className={cn(
          "flex min-w-0 flex-1 items-start gap-2.5 px-2.5 py-2 text-left",
          confirming ? "pr-18" : "pr-10",
        )}
        onClick={() => onSelect(session.id)}
        aria-current={isActive ? "true" : undefined}
        title={label}
      >
        <MessageSquare
          size={13}
          className="mt-0.5 shrink-0 text-primary"
          aria-hidden="true"
        />
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <p className="m-0 truncate text-xs font-medium text-foreground">
            {label}
          </p>
          <div className="flex items-center gap-1 text-[0.625rem] text-muted-foreground">
            {date && (
              <>
                <Clock size={10} aria-hidden="true" />
                <span>{date}</span>
              </>
            )}
            {session.message_count != null && session.message_count > 0 && (
              <span className="ml-auto rounded-full bg-muted px-1.5 py-px text-[0.625rem]">
                {session.message_count} msg{session.message_count !== 1 ? "s" : ""}
              </span>
            )}
          </div>
        </div>
      </button>

      {confirming ? (
        <div className="absolute right-2 top-1/2 flex -translate-y-1/2 items-center gap-1">
          <Button
            type="button"
            variant="destructive"
            size="icon-xs"
            className="cursor-pointer"
            onClick={(e) => {
              e.stopPropagation()
              setConfirming(false)
              onDelete(session.id)
            }}
            aria-label={`Confirm delete session: ${label}`}
            title="Confirm delete"
          >
            <Check size={12} />
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="icon-xs"
            className="cursor-pointer"
            onClick={(e) => {
              e.stopPropagation()
              setConfirming(false)
            }}
            aria-label="Cancel delete"
            title="Cancel"
          >
            <X size={12} />
          </Button>
        </div>
      ) : (
        <Button
          type="button"
          variant="ghost"
          size="icon-xs"
          className="absolute right-2 top-1/2 -translate-y-1/2 cursor-pointer rounded-md border border-border bg-muted opacity-0 transition-opacity group-hover/session:opacity-100 focus-visible:opacity-100 hover:bg-muted-foreground/15 hover:text-destructive"
          onClick={(e) => {
            e.stopPropagation()
            setConfirming(true)
          }}
          aria-label={`Delete session: ${label}`}
          title="Delete session"
        >
          <Trash2 size={12} />
        </Button>
      )}
    </div>
  )
})

// ---------------------------------------------------------------------------
// Main component
// ---------------------------------------------------------------------------

interface SessionSheetProps {
  open: boolean
  activeSessionId: string | null
  onOpenChange: (open: boolean) => void
  onNewSession: () => Promise<void>
  onSwitchSession: (sessionId: string) => Promise<void>
}

const PAGE_SIZE = 50
const SCROLL_THRESHOLD = 80

export const SessionSheet = memo(function SessionSheet({
  open,
  activeSessionId,
  onOpenChange,
  onNewSession,
  onSwitchSession,
}: SessionSheetProps) {
  const [sessions, setSessions] = useState<AgentSession[]>([])
  const [hasMore, setHasMore] = useState(true)
  const [isLoading, setIsLoading] = useState(false)
  const [isLoadingMore, setIsLoadingMore] = useState(false)
  const listRef = useRef<HTMLDivElement | null>(null)
  const offsetRef = useRef(0)
  const loadingRef = useRef(false)

  const loadSessions = useCallback(
    async (reset = false) => {
      if (loadingRef.current) return
      loadingRef.current = true

      const nextOffset = reset ? 0 : offsetRef.current
      if (reset) {
        setIsLoading(true)
        setSessions([])
        offsetRef.current = 0
        setHasMore(true)
      } else {
        setIsLoadingMore(true)
      }

      const result = await listSessions({ limit: PAGE_SIZE, offset: nextOffset })
      const page = result?.data ?? []

      setSessions((prev) => (reset ? page : [...prev, ...page]))
      offsetRef.current = nextOffset + page.length
      setHasMore(result?.has_more ?? false)

      if (reset) setIsLoading(false)
      setIsLoadingMore(false)
      loadingRef.current = false
    },
    [],
  )

  useEffect(() => {
    if (open) {
      void loadSessions(true)
    }
  }, [open])

  const handleScroll = useCallback(() => {
    const el = listRef.current
    if (!el || loadingRef.current || !hasMore) return
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - SCROLL_THRESHOLD) {
      void loadSessions(false)
    }
  }, [hasMore, loadSessions])

  useEffect(() => {
    if (!open || !hasMore) return
    const el = listRef.current
    if (!el) return
    el.addEventListener("scroll", handleScroll)
    return () => el.removeEventListener("scroll", handleScroll)
  }, [open, hasMore, handleScroll])

  const handleSelect = useCallback(
    async (id: string) => {
      onOpenChange(false)
      await onSwitchSession(id)
    },
    [onOpenChange, onSwitchSession],
  )

  const handleDelete = useCallback(
    async (id: string) => {
      // Optimistic removal — restored in place if the gateway call fails
      const index = sessions.findIndex((s) => s.id === id)
      const removed = sessions[index]
      setSessions((prev) => prev.filter((s) => s.id !== id))

      const deleted = await deleteSession(id)
      if (!deleted) {
        if (removed) {
          setSessions((prev) => {
            const next = [...prev]
            next.splice(Math.min(index, next.length), 0, removed)
            return next
          })
        }
        return
      }

      // If the active session was deleted, reset to a fresh chat
      if (id === activeSessionId) {
        onOpenChange(false)
        await onNewSession()
      }
    },
    [sessions, activeSessionId, onOpenChange, onNewSession],
  )

  const handleNewSession = useCallback(async () => {
    onOpenChange(false)
    await onNewSession()
  }, [onOpenChange, onNewSession])

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent
        side="left"
        showCloseButton={false}
        className="flex flex-col gap-0 border-r border-border bg-background p-0"
      >
        <SheetHeader className="flex-row items-center justify-between gap-2 border-b p-4 pr-3">
          <SheetTitle className="min-w-0 truncate text-sm font-semibold">
            Conversations
          </SheetTitle>
          <Button
            type="button"
            variant="ghost"
            size="icon-xs"
            onClick={() => onOpenChange(false)}
            aria-label="Close conversations"
            title="Close"
          >
            <X size={14} />
          </Button>
        </SheetHeader>

        {/* Primary action — kept away from the dismiss control */}
        <div className="border-b p-2">
          <Button
            id="new-session-btn"
            variant="outline"
            size="sm"
            className="h-8 w-full gap-1.5 text-xs"
            onClick={handleNewSession}
          >
            <MessageSquarePlus size={14} aria-hidden="true" />
            New chat
          </Button>
        </div>

        <div
          ref={listRef}
          className="scrollbar-thin flex flex-1 flex-col gap-1 overflow-y-auto p-2"
          role="list"
          aria-label="Conversation history"
        >
          {isLoading ? (
            Array.from({ length: 4 }).map((_, i) => (
              <div key={`skeleton-${i}`} className="flex items-center gap-2.5 px-2.5 py-2">
                <Skeleton className="size-4 rounded" />
                <div className="flex flex-1 flex-col gap-1">
                  <Skeleton className="h-3 w-[70%]" />
                  <Skeleton className="h-2.5 w-[40%]" />
                </div>
              </div>
            ))
          ) : sessions.length === 0 ? (
            <div className="flex flex-col items-center justify-center gap-2 p-8 text-center text-xs text-muted-foreground">
              <MessageSquare
                size={24}
                className="opacity-30"
                aria-hidden="true"
              />
              <p>No conversations yet</p>
            </div>
          ) : (
            <>
              {sessions.map((session) => (
                <SessionRow
                  key={session.id}
                  session={session}
                  isActive={session.id === activeSessionId}
                  onSelect={handleSelect}
                  onDelete={handleDelete}
                />
              ))}
              {isLoadingMore && (
                <div className="flex items-center gap-2.5 px-2.5 py-2">
                  <Skeleton className="size-4 rounded" />
                  <div className="flex flex-1 flex-col gap-1">
                    <Skeleton className="h-3 w-[70%]" />
                    <Skeleton className="h-2.5 w-[40%]" />
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
})
