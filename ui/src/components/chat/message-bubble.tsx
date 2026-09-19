import { memo, useState, useEffect, useCallback, Suspense, lazy } from "react"
import { Copy, Check, Wrench, CheckCircle, Brain, ChevronDown, FileText } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Avatar, AvatarFallback } from "@/components/ui/avatar"
import { cn } from "@/lib/utils"
import { TypingIndicator } from "./typing-indicator"
import type { AssistantMessage, ChatMessage, RunMeta, ToolMessage, UserMessage } from "@/types/chat"
const ReactMarkdown = lazy(async () => {
  const [{ default: ReactMarkdown }, { default: remarkGfm }] = await Promise.all([
    import("react-markdown"),
    import("remark-gfm"),
  ])
  return {
    default: ({ children }: { children: string }) => (
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{children}</ReactMarkdown>
    ),
  }
})

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const MIN_VALID_MS = 1_577_836_800_000 // Jan 1 2020

function formatRelativeTime(ms: number, now: number): string | null {
  try {
    if (now - ms < 10_000 && now >= ms) return "just now"
    const diff = now - ms
    const seconds = Math.floor(diff / 1000)
    const minutes = Math.floor(seconds / 60)
    const hours = Math.floor(minutes / 60)
    const days = Math.floor(hours / 24)
    if (days > 0) return `${days}d ago`
    if (hours > 0) return `${hours}h ago`
    if (minutes > 0) return `${minutes}m ago`
    return `${seconds}s ago`
  } catch {
    return null
  }
}

/** Ticking clock so relative timestamps ("5m ago") stay fresh over time. */
function useNow(intervalMs = 30_000): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])
  return now
}

/** Run duration for the summary line: "850ms", "12.4s", "2m 5s". */
function formatRunDuration(ms: number): string {
  if (ms < 1000) return `${Math.max(1, Math.round(ms))}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  const minutes = Math.floor(ms / 60_000)
  const seconds = Math.round((ms % 60_000) / 1000)
  return `${minutes}m ${seconds}s`
}

// ---------------------------------------------------------------------------
// Avatar
// ---------------------------------------------------------------------------

const AgentAvatar = memo(function AgentAvatar({
  active = false,
}: {
  active?: boolean
}): React.JSX.Element {
  return (
    <Avatar
      className={cn(
        "relative mt-0.5 size-6 shrink-0 text-[0.6rem]",
        active && "[&>span]:ring-2 [&>span]:ring-primary",
      )}
      aria-hidden="true"
    >
      <AvatarFallback className="bg-primary text-primary-foreground font-bold">
        H
      </AvatarFallback>
      {active && (
        <span className="animate-avatar-pulse absolute -inset-0.5 rounded-full border-2 border-primary" />
      )}
    </Avatar>
  )
})

// ---------------------------------------------------------------------------
// Copy button
// ---------------------------------------------------------------------------

const CopyButton = memo(function CopyButton({
  content,
}: {
  content: string
}): React.JSX.Element {
  const [copied, setCopied] = useState(false)

  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(content)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      /* clipboard may be restricted in some WebView2 contexts */
    }
  }, [content])

  return (
    <Button
      type="button"
      variant="ghost"
      size="icon-xs"
      onClick={handleCopy}
      title={copied ? "Copied!" : "Copy message"}
      aria-label={copied ? "Copied!" : "Copy message"}
      className="h-5 w-5 rounded-md border border-border bg-background text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100 hover:bg-muted hover:text-foreground"
    >
      {copied ? <Check size={12} /> : <Copy size={12} />}
    </Button>
  )
})

// ---------------------------------------------------------------------------
// Reasoning / Tool Accordion
// ---------------------------------------------------------------------------

interface CollapsibleToolCardProps {
  icon: React.ReactNode
  title: string
  detail?: string | null
  children: React.ReactNode
  active?: boolean
  defaultOpen?: boolean
  className?: string
}

const CollapsibleToolCard = memo(function CollapsibleToolCard({
  icon,
  title,
  detail,
  children,
  active,
  defaultOpen = false,
  className,
}: CollapsibleToolCardProps): React.JSX.Element {
  const [open, setOpen] = useState(defaultOpen)

  return (
    <div className={cn("w-3/4 min-w-0", className)}>
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className={cn(
          "flex w-full min-w-0 items-center gap-2 rounded-lg border border-border bg-card px-2 py-1.5 text-left text-xs transition-colors hover:bg-muted/50",
          open && "rounded-b-none border-b-0",
        )}
      >
        <span className={cn("shrink-0", active && "animate-pulse")}>{icon}</span>
        <span
          className={cn(
            "shrink-0 font-mono font-semibold",
            active ? "text-primary" : "text-foreground",
          )}
        >
          {title}
        </span>
        {detail ? (
          <span className="min-w-0 flex-1 truncate text-[0.7rem] text-muted-foreground/85">
            {detail.split("\n")[0]}
          </span>
        ) : null}
        <ChevronDown
          size={13}
          className={cn(
            "ml-auto shrink-0 text-muted-foreground transition-transform duration-200",
            open && "rotate-180",
          )}
        />
      </button>
      <div
        className={cn(
          "grid transition-[grid-template-rows] duration-200 ease-out",
          open ? "grid-rows-[1fr]" : "grid-rows-[0fr]",
        )}
      >
        <div className="min-h-0 overflow-hidden">
          <div className="rounded-b-lg border border-t-0 border-border bg-muted px-2 py-1.5">
            {children}
          </div>
        </div>
      </div>
    </div>
  )
})

const ReasoningAccordion = memo(function ReasoningAccordion({
  text,
  active = false,
  standalone = false,
  flat = false,
}: {
  text: string
  active?: boolean
  standalone?: boolean
  /** Flat mode: no avatar gutter — the row spans the full width. */
  flat?: boolean
}): React.JSX.Element {
  const content = (
    <CollapsibleToolCard
      icon={<Brain size={13} />}
      title={active ? "Thinking..." : "Reasoning"}
      detail={text}
      active={active}
      className={standalone ? undefined : "mb-2"}
    >
      <pre className="m-0 whitespace-pre-wrap font-mono text-[0.6875rem] text-muted-foreground">
        {text}
      </pre>
    </CollapsibleToolCard>
  )

  if (standalone) {
    if (flat) return <div className="animate-msg-in min-w-0 px-3">{content}</div>
    return (
      <div className="animate-msg-in grid w-full grid-cols-[2rem_1fr] items-start gap-2">
        <div aria-hidden="true" />
        <div className="min-w-0">{content}</div>
      </div>
    )
  }
  return content
})

// ---------------------------------------------------------------------------
// User bubble
// ---------------------------------------------------------------------------

const UserBubble = memo(function UserBubble({
  msg,
  flat = false,
}: {
  msg: UserMessage
  /** Flat mode (opencode-style): full-width, input-field background, no side. */
  flat?: boolean
}): React.JSX.Element {
  const now = useNow()
  const isValid = msg.timestamp >= MIN_VALID_MS
  const relTime = isValid ? formatRelativeTime(msg.timestamp, now) : null

  // Strip internal app context and reminders from the user bubble view
  const displayContent = msg.content
    .replace(/\[(EXCEL_CONTEXT|WORD_CONTEXT|DECK_CONTEXT)\][\s\S]*?(?:\[\/(?:EXCEL_CONTEXT|WORD_CONTEXT|DECK_CONTEXT)\]|$)/s, "")
    .replace(/\[REMINDER\][\s\S]*?(?:\[\/REMINDER\]|$)/s, "")
    .trim()

  return (
    <div
      className={cn(
        "animate-msg-in flex w-full min-w-0 flex-col gap-0.5",
        flat ? "items-stretch" : "items-end",
      )}
    >
      <div
        className={cn(
          "break-words text-[0.8125rem] leading-relaxed",
          flat
            ? "w-full bg-muted px-3 py-2 text-foreground"
            : "max-w-[min(85%,340px)] rounded-2xl rounded-br-md bg-primary px-3 py-2 text-primary-foreground shadow-sm",
        )}
      >
        {msg.attachments && msg.attachments.length > 0 && (
          <div className="mb-1.5 flex flex-wrap gap-1.5">
            {msg.attachments.map((attachment, index) =>
              attachment.mime.startsWith("image/") ? (
                <img
                  key={`${attachment.name}-${index}`}
                  src={attachment.dataUrl}
                  alt={attachment.name}
                  title={attachment.name}
                  className={cn(
                    "max-h-32 max-w-full rounded-md object-cover",
                    flat ? "border border-border" : "border border-primary-foreground/20",
                  )}
                />
              ) : (
                <span
                  key={`${attachment.name}-${index}`}
                  className={cn(
                    "inline-flex items-center gap-1.5 rounded-md border px-2 py-1 text-[0.6875rem]",
                    flat
                      ? "border-border bg-card text-muted-foreground"
                      : "border-primary-foreground/20 bg-primary-foreground/10",
                  )}
                  title={attachment.name}
                >
                  <FileText size={13} aria-hidden="true" />
                  <span className="max-w-40 truncate">{attachment.name}</span>
                  {attachment.pageImages && (
                    <span className="opacity-70">({attachment.pageImages.length}p)</span>
                  )}
                </span>
              ),
            )}
          </div>
        )}
        {displayContent && <p className="m-0 whitespace-pre-wrap">{displayContent}</p>}
      </div>
      {relTime && (
        <time
          className={cn("text-[0.5625rem] text-muted-foreground", flat ? "ml-3" : "mr-1")}
          dateTime={new Date(msg.timestamp).toISOString()}
        >
          {relTime}
        </time>
      )}
    </div>
  )
})

// ---------------------------------------------------------------------------
// Assistant bubble
// ---------------------------------------------------------------------------

const AssistantBubble = memo(function AssistantBubble({
  msg,
  isLast,
  isLoading,
  suppressContent = false,
  runMeta,
  flat = false,
}: {
  msg: AssistantMessage
  isLast: boolean
  isLoading: boolean
  /**
   * The step's commentary text moved into a following tool card
   * (MessageList linkage) — render only the reasoning trace here.
   */
  suppressContent?: boolean
  /**
   * Aggregated metadata of the last complete run — shown under the final
   * assistant bubble only (opencode-style: model · tokens · duration).
   */
  runMeta?: RunMeta | null
  /** Flat mode (opencode-style): full-width, no avatar, no card chrome. */
  flat?: boolean
}): React.JSX.Element {
  const now = useNow()
  const isActive = isLoading && isLast && msg.streaming === true
  const hasReasoning = !!msg.reasoning
  const hasContent = !!msg.content && !suppressContent
  const hasImages = !!msg.images && msg.images.length > 0
  const showTyping = !msg.content && msg.streaming === true && !hasReasoning
  const showBubble = hasContent || showTyping || hasImages
  const relTime =
    msg.timestamp >= MIN_VALID_MS ? formatRelativeTime(msg.timestamp, now) : null
  const runMetaLine = runMeta
    ? [
        runMeta.tokens > 0 ? `${runMeta.tokens.toLocaleString()} tokens` : null,
        formatRunDuration(runMeta.durationMs),
      ]
        .filter(Boolean)
        .join(" · ")
    : null

  return (
    <div className={cn("animate-msg-in flex w-full min-w-0 flex-col", hasReasoning ? "gap-1.5" : "gap-0.5")}>
      {/* Thinking renders as its own tool-card row, outside the bubble — it
          is model activity, not message content (box-in-a-box read badly). */}
      {hasReasoning && (
        <ReasoningAccordion
          text={msg.reasoning ?? ""}
          active={isActive && !msg.content}
          standalone
          flat={flat}
        />
      )}
      {showBubble && (
        <div className="flex w-full min-w-0 items-start gap-2">
          {!flat && <AgentAvatar active={isActive} />}
          <div
            className={cn(
              "group relative min-w-0 text-[0.8125rem] leading-relaxed",
              flat
                ? "w-full px-3 py-0.5 text-foreground"
                : "max-w-[min(85%,340px)] rounded-2xl rounded-bl-md border border-border bg-card p-3 text-card-foreground shadow-sm",
            )}
          >
            {hasContent && !msg.streaming && (
              <div className="absolute top-1 right-1">
                <CopyButton content={msg.content} />
              </div>
            )}
            {showTyping ? (
              <TypingIndicator />
            ) : hasContent ? (
              <Suspense fallback={<p className="m-0 whitespace-pre-wrap break-words">{msg.content}</p>}>
                <div className="markdown-body">
                  <ReactMarkdown>{msg.content}</ReactMarkdown>
                </div>
              </Suspense>
            ) : null}
            {msg.images && msg.images.length > 0 && (
              <div className={cn("flex flex-wrap gap-1.5", hasContent && "mt-2")}>
                {msg.images.map((image, index) => (
                  <img
                    key={index}
                    src={image}
                    alt="Generated by the model"
                    className="max-h-48 max-w-full rounded-md border border-border object-contain"
                  />
                ))}
              </div>
            )}
          </div>
        </div>
      )}
      {showBubble && (relTime || runMetaLine) ? (
        !msg.streaming && (
          <div className={cn("flex items-center gap-1.5 text-[0.5625rem] text-muted-foreground", flat ? "ml-3" : "ml-8")}>
            {relTime && (
              <time dateTime={new Date(msg.timestamp).toISOString()}>{relTime}</time>
            )}
            {relTime && runMetaLine && <span aria-hidden="true">·</span>}
            {runMetaLine && <span>{runMetaLine}</span>}
          </div>
        )
      ) : null}
    </div>
  )
})

// ---------------------------------------------------------------------------
// Tool bubble — collapsible activity row
// ---------------------------------------------------------------------------
function splitToolPreview(preview: string): { summary: string; data: string | null } {
  const sep = preview.indexOf("\n{")
  if (sep < 0) return { summary: preview, data: null }
  return { summary: preview.slice(0, sep), data: preview.slice(sep + 1) }
}

const ToolBubble = memo(function ToolBubble({
  msg,
  narration,
  flat = false,
}: {
  msg: ToolMessage
  /** The step's commentary text, moved inside the card (see MessageList). */
  narration?: string
  /** Flat mode: no avatar gutter — the card spans the full width. */
  flat?: boolean
}): React.JSX.Element {
  if (
    msg.toolName === "_thinking" ||
    msg.toolName === "reasoning" ||
    msg.toolName === "thinking"
  ) {
    return (
      <ReasoningAccordion
        text={msg.preview || ""}
        active={!msg.completed}
        standalone
        flat={flat}
      />
    )
  }

  const { summary, data } = splitToolPreview(msg.preview ?? "")

  const card = (
    <CollapsibleToolCard
      icon={
        msg.completed ? (
          <CheckCircle size={13} className="text-emerald-500" />
        ) : (
          <Wrench size={13} className="text-primary" />
        )
      }
      title={msg.toolName}
      detail={summary}
      active={!msg.completed}
    >
      {narration ? (
        <p className="m-0 mb-1.5 whitespace-pre-wrap break-words border-b border-border/60 pb-1.5 text-[0.7rem] italic text-muted-foreground">
          {narration}
        </p>
      ) : null}
      <p className="m-0 whitespace-pre-wrap break-words text-[0.7rem] text-foreground/90">
        {summary}
      </p>
      {data !== null && (
        <pre className="m-0 mt-1.5 max-h-28 overflow-y-auto whitespace-pre-wrap break-all rounded-md border border-border bg-muted p-1.5 font-mono text-[0.625rem] leading-snug text-muted-foreground">
          {data}
        </pre>
      )}
    </CollapsibleToolCard>
  )

  if (flat) return <div className="animate-msg-in min-w-0 px-3">{card}</div>
  return (
    <div className="animate-msg-in grid w-full grid-cols-[2rem_1fr] items-start gap-2">
      <div aria-hidden="true" />
      {card}
    </div>
  )
})

// ---------------------------------------------------------------------------
// MessageBubble — routes to the correct sub-component based on role
// ---------------------------------------------------------------------------

interface MessageBubbleProps {
  msg: ChatMessage
  isLast: boolean
  isLoading: boolean
  /** Commentary of the preceding assistant step (tool cards only). */
  narration?: string
  /** Render reasoning only — content moved into a tool card. */
  suppressContent?: boolean
  /** Last-run summary line (final assistant bubble only). */
  runMeta?: RunMeta | null
  /** Flat mode (opencode-style single-column flow). */
  flat?: boolean
}

export const MessageBubble = memo(function MessageBubble({
  msg,
  isLast,
  isLoading,
  narration,
  suppressContent,
  runMeta,
  flat = false,
}: MessageBubbleProps): React.JSX.Element {
  if (msg.role === "user") {
    return <UserBubble msg={msg as UserMessage} flat={flat} />
  }

  if (msg.role === "tool") {
    return <ToolBubble msg={msg as ToolMessage} narration={narration} flat={flat} />
  }

  return (
    <AssistantBubble
      msg={msg as AssistantMessage}
      isLast={isLast}
      isLoading={isLoading}
      suppressContent={suppressContent}
      runMeta={runMeta}
      flat={flat}
    />
  )
})
