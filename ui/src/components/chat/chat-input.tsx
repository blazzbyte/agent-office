import {
  useRef,
  useEffect,
  useLayoutEffect,
  useState,
  useCallback,
  forwardRef,
  useImperativeHandle,
} from "react"
import { ArrowUp, Square, Paperclip, X, FileText } from "lucide-react"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"
import { useConnectionStore, selectProviderModelInput } from "@/store/connection.store"
import {
  ATTACHMENT_ACCEPT,
  MAX_ATTACHMENT_BYTES,
  readFileAsDataUrl,
} from "@/lib/attachments"
import type { MessageAttachment } from "@/types/chat"
import { logger } from "@/lib/utils/logger"

// ---------------------------------------------------------------------------
// Imperative handle — lets parent components focus or set text programmatically
// ---------------------------------------------------------------------------

export interface ChatInputHandle {
  focus(): void
  setText(text: string): void
  clear(): void
}

// ---------------------------------------------------------------------------
// Props
// ---------------------------------------------------------------------------

interface ChatInputProps {
  isLoading: boolean
  onSubmit: (message: string, attachments: MessageAttachment[]) => void
  onAbort: () => void
  /** Optional suggestion text injected from the empty state */
  pendingSuggestion?: string | null
  onSuggestionConsumed?: () => void
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

export const ChatInput = forwardRef<ChatInputHandle, ChatInputProps>(
  function ChatInput(
    { isLoading, onSubmit, onAbort, pendingSuggestion, onSuggestionConsumed },
    ref,
  ): React.JSX.Element {
    const [value, setValue] = useState("")
    const [attachments, setAttachments] = useState<MessageAttachment[]>([])
    const textareaRef = useRef<HTMLTextAreaElement>(null)
    const fileInputRef = useRef<HTMLInputElement>(null)
    // Stable array reference — derive capability flags locally (selecting a
    // fresh object from a zustand hook would re-render-loop the component).
    const modelInput = useConnectionStore(selectProviderModelInput)
    const modelAcceptsMedia =
      modelInput.includes("image") || modelInput.includes("pdf")

    // Consume suggestion from empty state
    useEffect(() => {
      if (!pendingSuggestion) return
      setValue(pendingSuggestion)
      onSuggestionConsumed?.()
      requestAnimationFrame(() => {
        textareaRef.current?.focus()
      })
    }, [pendingSuggestion, onSuggestionConsumed])

    // Auto-resize: update textarea height in a layout effect to avoid
    // reflow on every keystroke (same pattern as desktop agents).
    const autoResize = useCallback((): void => {
      const el = textareaRef.current
      if (!el) return
      el.style.height = "auto"
      el.style.height = `${Math.min(el.scrollHeight, 140)}px`
    }, [])

    useLayoutEffect(() => {
      autoResize()
    }, [value, autoResize])

    // Imperative handle
    useImperativeHandle(ref, () => ({
      focus() {
        textareaRef.current?.focus()
      },
      setText(text: string) {
        setValue(text)
        requestAnimationFrame(() => {
          textareaRef.current?.focus()
        })
      },
      clear() {
        setValue("")
        setAttachments([])
        if (textareaRef.current) {
          textareaRef.current.style.height = "auto"
        }
      },
    }))

    const canSend = (value.trim().length > 0 || attachments.length > 0) && !isLoading

    const handleSubmit = useCallback((): void => {
      if (!canSend) return
      const text = value.trim()
      const pending = attachments
      setValue("")
      setAttachments([])
      if (textareaRef.current) {
        textareaRef.current.style.height = "auto"
      }
      onSubmit(text, pending)
    }, [canSend, value, attachments, onSubmit])

    const handleKeyDown = useCallback(
      (e: React.KeyboardEvent<HTMLTextAreaElement>): void => {
        if (e.key === "Enter" && !e.shiftKey) {
          e.preventDefault()
          handleSubmit()
        }
      },
      [handleSubmit],
    )

    const addFiles = useCallback(async (files: FileList | null): Promise<void> => {
      if (!files) return
      for (const file of Array.from(files)) {
        const isImage = file.type.startsWith("image/")
        const isPdf = file.type === "application/pdf"
        if (!isImage && !isPdf) continue
        if (file.size > MAX_ATTACHMENT_BYTES) {
          logger.warn("chat-input: attachment too large, skipped", {
            name: file.name,
            size: file.size,
          })
          continue
        }
        try {
          const dataUrl = await readFileAsDataUrl(file)
          setAttachments((prev) => [
            ...prev,
            { name: file.name, mime: file.type, dataUrl },
          ])
        } catch (err) {
          logger.warn("chat-input: could not read attachment", { err, name: file.name })
        }
      }
    }, [])

    const removeAttachment = useCallback((index: number): void => {
      setAttachments((prev) => prev.filter((_, i) => i !== index))
    }, [])

    return (
      <div className="flex flex-col gap-1 p-2.5">
        {/* Pending attachments preview */}
        {attachments.length > 0 && (
          <div className="flex flex-wrap gap-1.5 px-1" aria-label="Attachments">
            {attachments.map((attachment, index) => (
              <span
                key={`${attachment.name}-${index}`}
                className="group/att relative inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-1.5 py-1 text-[0.625rem] text-muted-foreground"
                title={attachment.name}
              >
                {attachment.mime.startsWith("image/") ? (
                  <img
                    src={attachment.dataUrl}
                    alt={attachment.name}
                    className="size-8 rounded-sm object-cover"
                  />
                ) : (
                  <FileText size={14} className="shrink-0" aria-hidden="true" />
                )}
                <span className="max-w-28 truncate">{attachment.name}</span>
                <button
                  type="button"
                  className="rounded-full p-0.5 hover:bg-muted hover:text-foreground"
                  onClick={() => removeAttachment(index)}
                  aria-label={`Remove ${attachment.name}`}
                >
                  <X size={11} />
                </button>
              </span>
            ))}
          </div>
        )}

        <div
          className={cn(
            "flex min-h-[44px] items-end gap-1.5 rounded-xl border border-input bg-muted p-1 pl-2.5 transition-[border-color,box-shadow] focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/20",
          )}
        >
          <input
            ref={fileInputRef}
            type="file"
            accept={ATTACHMENT_ACCEPT}
            multiple
            className="hidden"
            aria-hidden="true"
            tabIndex={-1}
            onChange={(e) => {
              void addFiles(e.target.files)
              e.target.value = "" // re-selecting the same file must re-fire
            }}
          />
          <textarea
            ref={textareaRef}
            id="chat-message-input"
            value={value}
            onChange={(e) => setValue(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder={
              isLoading ? "Agent Office is responding — type your next message…" : "Message Agent Office…"
            }
            rows={1}
            aria-label="Message input"
            aria-multiline="true"
            className="max-h-[140px] min-h-0 w-full flex-1 resize-none border-0 bg-transparent py-2 text-[0.8125rem] leading-relaxed text-foreground outline-none placeholder:text-muted-foreground disabled:cursor-not-allowed"
          />
          <div className="flex shrink-0 items-center gap-0.5 pb-1 pr-0.5">
            <Button
              type="button"
              id="chat-attach-btn"
              variant="ghost"
              size="icon-sm"
              className="rounded-full text-muted-foreground"
              onClick={() => fileInputRef.current?.click()}
              aria-label="Attach images or PDF"
              title={
                modelAcceptsMedia
                  ? "Attach images or PDF (sent per the model's capabilities)"
                  : "Attach images or PDF — the active model is text-only, attachments may be ignored"
              }
            >
              <Paperclip size={14} />
            </Button>
            {isLoading ? (
              <Button
                type="button"
                id="chat-stop-btn"
                size="icon-sm"
                className="rounded-full"
                onClick={onAbort}
                aria-label="Stop generation"
                title="Stop generation"
              >
                <Square size={14} />
              </Button>
            ) : (
              <Button
                type="button"
                id="chat-send-btn"
                size="icon-sm"
                className="rounded-full"
                onClick={handleSubmit}
                disabled={!canSend}
                aria-label="Send message"
                title="Send message (Enter)"
              >
                <ArrowUp size={14} />
              </Button>
            )}
          </div>
        </div>
      </div>
    )
  },
)
