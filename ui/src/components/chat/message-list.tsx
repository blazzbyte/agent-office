import { memo } from "react"
import { Avatar, AvatarFallback } from "@/components/ui/avatar"
import { MessageBubble } from "./message-bubble"
import { TypingIndicator } from "./typing-indicator"
import { useUiStore, selectRenderStyle } from "@/store/ui.store"
import type { AssistantMessage, ChatMessage, RunMeta, ToolMessage } from "@/types/chat"
import type { ToolProgress } from "@/store/chat.store"

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

interface MessageListProps {
  messages: ChatMessage[]
  isLoading: boolean
  toolProgress: ToolProgress | null
}

/** Tool messages that render as thinking traces, not activity cards. */
const REASONING_TOOL_NAMES = new Set(["_thinking", "reasoning", "thinking"])

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export const MessageList = memo(function MessageList({
  messages,
  isLoading,
  toolProgress,
}: MessageListProps): React.JSX.Element {
  const flat = useUiStore(selectRenderStyle) === "flat"

  // Filter out empty assistant streaming bubbles that haven't received
  // any delta yet — show the TypingIndicator instead.
  const visibleMessages = messages.filter((msg) => {
    if (msg.role !== "assistant") return true
    return (msg.content || "").length > 0
  })

  // -------------------------------------------------------------------------
  // Step-commentary linkage: an assistant step whose text merely narrates
  // tool calls ("I'll now read the range…") is noise between activities.
  // When real tool cards immediately follow the step, that text moves INSIDE
  // the first card's collapsible area (opencode-style: commentary belongs to
  // the activity), and the bubble renders its reasoning trace only — or
  // drops entirely when it has neither reasoning nor content of its own.
  // Final-answer steps (no tool card after them) keep their normal bubble.
  // -------------------------------------------------------------------------
  const narrationByToolId = new Map<string, string>()
  const suppressContentIds = new Set<string>()
  const dropIds = new Set<string>()

  visibleMessages.forEach((msg, idx) => {
    if (msg.role !== "assistant") return
    const text = (msg.content ?? "").trim()
    if (!text) return

    // Find the first following message; it must be a REAL tool card (reasoning
    // traces don't count as the step's activity).
    const next = visibleMessages[idx + 1]
    if (next?.role !== "tool") return
    const toolName = (next as ToolMessage).toolName ?? ""
    if (REASONING_TOOL_NAMES.has(toolName)) return

    narrationByToolId.set(next.id, text)
    const assistant = msg as AssistantMessage
    if (assistant.reasoning) suppressContentIds.add(msg.id)
    else dropIds.add(msg.id)
  })

  const renderMessages =
    dropIds.size > 0
      ? visibleMessages.filter((m) => !dropIds.has(m.id))
      : visibleMessages

  const lastMsg = renderMessages[renderMessages.length - 1]
  const lastIsAssistant = lastMsg?.role === "assistant"

  // -------------------------------------------------------------------------
  // Last-run summary: aggregate the trailing run's assistant steps
  // (everything after the last user message) into one metadata line —
  // tokens · duration — shown under the final assistant bubble, and only
  // once the run is fully complete (nothing streaming). Per-step durations
  // include tool time (host boundary accounting), so the sum is the full
  // run wall time. The model id stays in msg.usage (metadata only).
  // -------------------------------------------------------------------------
  // The summary anchors to the final VISIBLE bubble — a reasoning-only
  // remnant (narration moved into a tool card) carries no timestamp/meta.
  let lastAssistantIdx = -1
  for (let i = renderMessages.length - 1; i >= 0; i--) {
    const m = renderMessages[i]
    if (
      m.role === "assistant" &&
      (m as AssistantMessage).content &&
      !suppressContentIds.has(m.id)
    ) {
      lastAssistantIdx = i
      break
    }
  }

  let runMeta: RunMeta | null = null
  if (!isLoading && lastAssistantIdx !== -1) {
    let tokens = 0
    let durationMs = 0
    let sawUsage = false
    for (let i = renderMessages.length - 1; i >= 0; i--) {
      const m = renderMessages[i]
      if (m.role === "user") break
      if (m.role !== "assistant") continue
      const usage = (m as AssistantMessage).usage
      if (!usage) continue
      sawUsage = true
      tokens += usage.total_tokens ?? 0
      durationMs += usage.duration_ms ?? 0
    }
    if (sawUsage && durationMs > 0) runMeta = { tokens, durationMs }
  }

  return (
    <div
      className={flat
        ? "flex min-h-full flex-col gap-4 pb-2"
        : "flex min-h-full flex-col gap-3 p-3 pb-2"}
      role="log"
      aria-live={isLoading ? "off" : "polite"}
      aria-label="Conversation"
      style={{ overflowAnchor: "none" }}
    >
      {renderMessages.map((msg, idx) => (
        <div
          key={msg.id}
          style={{ contentVisibility: "auto", containIntrinsicHeight: "80px" }}
        >
          <MessageBubble
            msg={msg}
            isLast={idx === renderMessages.length - 1}
            isLoading={isLoading}
            narration={narrationByToolId.get(msg.id)}
            suppressContent={suppressContentIds.has(msg.id)}
            runMeta={idx === lastAssistantIdx ? runMeta : null}
            flat={flat}
          />
        </div>
      ))}

      {/* In-flight tools render as their own activity card (the ToolMessage
          is created at tool start and streams its delta into the header) —
          a separate progress bar would duplicate the same running tool. */}

      {/* Typing indicator: show when loading but the assistant bubble is still
          empty (first delta hasn't arrived yet) */}
      {isLoading && !lastIsAssistant && !toolProgress && (
        flat ? (
          <div className="animate-msg-in px-3 py-1">
            <TypingIndicator />
          </div>
        ) : (
          <div className="animate-msg-in flex w-full min-w-0 items-end gap-2">
            <Avatar className="relative mt-0.5 size-6 text-[0.6rem]" aria-hidden="true">
              <AvatarFallback className="bg-primary text-primary-foreground font-bold">
                H
              </AvatarFallback>
              <span className="animate-avatar-pulse absolute -inset-0.5 rounded-full border-2 border-primary" />
            </Avatar>
            <div className="min-w-0 max-w-[min(85%,340px)] rounded-2xl rounded-bl-md border border-border bg-card p-2.5 text-[0.8125rem] text-card-foreground shadow-sm">
              <TypingIndicator />
            </div>
          </div>
        )
      )}
    </div>
  )
})
