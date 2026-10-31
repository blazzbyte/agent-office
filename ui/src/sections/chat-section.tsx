import { useEffect, useRef, useState, useCallback } from "react"
import { ArrowDown } from "lucide-react"
import {
  ChatHeader,
  ChatInput,
  ChatEmptyState,
  ConnectionSheet,
  ContextMetricsBar,
  MessageList,
} from "@/components/chat"
import type { ChatInputHandle } from "@/components/chat"
import { SessionSheet } from "@/components/chat/session-sheet"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import { useChat } from "@/hooks/use-chat"
import { useConnection } from "@/hooks/use-connection"
import { useBridge } from "@/hooks/use-bridge"
import { useSession } from "@/hooks/use-session"
import type { MessageAttachment } from "@/types/chat"
import { useT } from "@/i18n"

// Distance (px) from the bottom that still counts as "at the bottom"
const BOTTOM_THRESHOLD_PX = 80

// ---------------------------------------------------------------------------
// MessageListSkeleton — placeholder bubbles while a session history loads
// ---------------------------------------------------------------------------

function MessageListSkeleton(): React.JSX.Element {
  return (
    <div
      className="flex min-h-full flex-col gap-3 p-3 pb-2"
      aria-hidden="true"
    >
      <div className="flex justify-end">
        <Skeleton className="h-9 w-[55%] rounded-2xl rounded-br-md" />
      </div>
      <div className="flex items-start gap-2">
        <Skeleton className="size-6 shrink-0 rounded-full" />
        <Skeleton className="h-16 w-[75%] rounded-2xl rounded-bl-md" />
      </div>
      <div className="flex justify-end">
        <Skeleton className="h-9 w-[45%] rounded-2xl rounded-br-md" />
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// ChatSection — root section for the taskpane chat UI.
//
// Layout:
//   ┌─────────────────────┐
//   │  ChatHeader         │ ← fixed: brand + connection badge + history + new chat
//   ├─────────────────────┤
//   │  MessageList        │ ← flex-1, scrollable
//   │   or EmptyState     │
//   ├─────────────────────┤
//   │  ChatInput          │ ← fixed at bottom
//   └─────────────────────┘
//
// A SessionSheet slides in from the left for session navigation.
// ---------------------------------------------------------------------------

export function ChatSection(): React.JSX.Element {
  const t = useT()
  const inputRef = useRef<ChatInputHandle>(null)
  const [pendingSuggestion, setPendingSuggestion] = useState<string | null>(null)
  const [sheetOpen, setSheetOpen] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)

  const { messages, isLoading, toolProgress, sendMessage, stopGeneration } = useChat()
  const { status, gatewayVersion, hostApp, providerConfigured, providerModel } = useConnection()
  const { status: bridgeStatus, version: bridgeVersion } = useBridge()
  const { sessionId, isInitializing, newSession, switchSession } = useSession()

  const hasMessages = messages.length > 0

  // ---------------------------------------------------------------------------
  // Scroll management — only auto-scroll while the user is at the bottom.
  // Otherwise a floating "jump to bottom" button appears instead of yanking
  // the viewport on every streamed delta.
  // ---------------------------------------------------------------------------

  const scrollRef = useRef<HTMLDivElement>(null)
  const [stickToBottom, setStickToBottom] = useState(true)
  const [showJumpToBottom, setShowJumpToBottom] = useState(false)

  const scrollToBottom = useCallback((behavior: ScrollBehavior = "smooth"): void => {
    const el = scrollRef.current
    if (!el) return
    el.scrollTo({ top: el.scrollHeight, behavior })
  }, [])

  const pinToBottom = useCallback((): void => {
    setStickToBottom(true)
    setShowJumpToBottom(false)
  }, [])

  const handleScroll = useCallback((): void => {
    const el = scrollRef.current
    if (!el) return
    const atBottom =
      el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_THRESHOLD_PX
    setStickToBottom(atBottom)
    if (atBottom) {
      setShowJumpToBottom(false)
    } else if (isLoading) {
      // Scrolled up while a turn is streaming — new content is arriving below
      setShowJumpToBottom(true)
    }
  }, [isLoading])

  const jumpToBottom = useCallback((): void => {
    pinToBottom()
    scrollToBottom("smooth")
  }, [pinToBottom, scrollToBottom])

  // Follow new content only while pinned to the bottom (DOM sync only)
  useEffect(() => {
    if (stickToBottom) {
      scrollToBottom(isLoading ? "instant" : "smooth")
    }
  }, [messages, isLoading, toolProgress, stickToBottom, scrollToBottom])

  // ---------------------------------------------------------------------------
  // Handlers
  // ---------------------------------------------------------------------------

  const handleSubmit = useCallback(
    async (text: string, attachments: MessageAttachment[]): Promise<void> => {
      // Sending always pins the view to the latest message
      pinToBottom()
      await sendMessage(text, attachments)
    },
    [pinToBottom, sendMessage],
  )

  const handleSuggestion = useCallback((text: string): void => {
    setPendingSuggestion(text)
    requestAnimationFrame(() => inputRef.current?.focus())
  }, [])

  const handleSuggestionConsumed = useCallback((): void => {
    setPendingSuggestion(null)
  }, [])

  const handleNewChat = useCallback(async (): Promise<void> => {
    // Stop the visible stream before resetting — the gateway may keep
    // processing in the background, but this view stops receiving data.
    if (isLoading) stopGeneration()
    pinToBottom()
    await newSession()
    inputRef.current?.focus()
  }, [isLoading, stopGeneration, pinToBottom, newSession])

  const handleSwitchSession = useCallback(
    async (id: string): Promise<void> => {
      if (isLoading) stopGeneration()
      pinToBottom()
      await switchSession(id)
    },
    [isLoading, stopGeneration, pinToBottom, switchSession],
  )

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  return (
    <div className="flex h-dvh max-h-dvh flex-col overflow-hidden bg-background">
      {/* Session history sheet (slides in from left) */}
      <SessionSheet
        open={sheetOpen}
        activeSessionId={sessionId}
        onOpenChange={setSheetOpen}
        onNewSession={handleNewChat}
        onSwitchSession={handleSwitchSession}
      />

      {/* Connection settings sheet (provider API key + base URL + model) */}
      <ConnectionSheet open={settingsOpen} onOpenChange={setSettingsOpen} />

      {/* Header */}
      <ChatHeader
        status={status}
        gatewayVersion={gatewayVersion}
        hostApp={hostApp}
        providerConfigured={providerConfigured}
        providerModel={providerModel}
        bridgeStatus={bridgeStatus}
        bridgeVersion={bridgeVersion}
        onHistoryClick={() => setSheetOpen(true)}
        onNewChatClick={handleNewChat}
        onSettingsClick={() => setSettingsOpen(true)}
      />

      {/* Message area */}
      <div className="relative flex min-h-0 flex-1 flex-col">
        <div
          ref={scrollRef}
          onScroll={handleScroll}
          className="scrollbar-thin flex-1 overflow-y-auto overflow-x-hidden overscroll-contain"
        >
          {isInitializing && !hasMessages ? (
            <MessageListSkeleton />
          ) : hasMessages ? (
            <MessageList
              messages={messages}
              isLoading={isLoading}
              toolProgress={toolProgress}
            />
          ) : (
            <ChatEmptyState onSelectSuggestion={handleSuggestion} />
          )}
        </div>

        {showJumpToBottom && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="absolute bottom-3 left-1/2 h-7 -translate-x-1/2 gap-1 text-xs shadow-md"
            onClick={jumpToBottom}
            aria-label={t("shell.jumpToLatest")}
          >
            <ArrowDown size={13} aria-hidden="true" />
            {t("shell.newMessages")}
          </Button>
        )}
      </div>

      {/* Input */}
      <div className="shrink-0 border-t bg-background">
        <ChatInput
          ref={inputRef}
          isLoading={isLoading}
          onSubmit={handleSubmit}
          onAbort={stopGeneration}
          pendingSuggestion={pendingSuggestion}
          onSuggestionConsumed={handleSuggestionConsumed}
        />
        <ContextMetricsBar />
      </div>
    </div>
  )
}
