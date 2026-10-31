import { memo } from "react"
import { useT } from "@/i18n"

// ---------------------------------------------------------------------------
// Typing indicator — three animated dots shown while the model generates
// ---------------------------------------------------------------------------

export const TypingIndicator = memo(function TypingIndicator(): React.JSX.Element {
  const t = useT()
  return (
    <div
      className="inline-flex items-center gap-1 py-1"
      aria-label={t("messages.typing")}
      role="status"
    >
      <span className="size-1.5 animate-typing-bounce rounded-full bg-primary [animation-delay:0ms]" />
      <span className="size-1.5 animate-typing-bounce rounded-full bg-primary [animation-delay:150ms]" />
      <span className="size-1.5 animate-typing-bounce rounded-full bg-primary [animation-delay:300ms]" />
    </div>
  )
})
