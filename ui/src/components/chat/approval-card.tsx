import { memo, useState } from "react"
import { Button } from "@/components/ui/button"
import { useChatStore, selectPendingApproval } from "@/store/chat.store"
import { sendToolApproval, type ApprovalDecision } from "@/services/session.service"
import { useT } from "@/i18n"

/** Compact, readable rendering of the tool arguments. */
function formatArgs(args: unknown): string {
  if (args == null) return ""
  try {
    const text = JSON.stringify(args, null, 1)
    return text === "{}" ? "" : text
  } catch {
    return ""
  }
}

export const ApprovalCard = memo(function ApprovalCard(): React.JSX.Element | null {
  const t = useT()
  const pending = useChatStore(selectPendingApproval)
  const sessionId = useChatStore((s) => s.activeSessionId)
  const clear = useChatStore((s) => s.setPendingApproval)
  const [sending, setSending] = useState(false)

  if (!pending || !sessionId) return null

  const decide = (decision: ApprovalDecision): void => {
    setSending(true)
    void sendToolApproval(sessionId, pending.callId, decision).finally(() => {
      setSending(false)
      clear(null)
    })
  }

  const args = formatArgs(pending.args)

  return (
    <div
      role="alertdialog"
      aria-label={t("messages.approval.title")}
      className="flex flex-col gap-1.5 border-b bg-muted/40 px-3 py-2"
    >
      <p className="m-0 text-xs">
        {t("messages.approval.title")}{" "}
        <code className="rounded bg-muted px-1 py-0.5 font-mono text-[0.6875rem]">
          {pending.toolName}
        </code>
      </p>
      {args && (
        <pre className="scrollbar-thin m-0 max-h-24 overflow-auto rounded border bg-background p-1.5 font-mono text-[0.625rem] leading-snug whitespace-pre-wrap break-all">
          {args}
        </pre>
      )}
      <div className="flex flex-wrap gap-1.5">
        <Button
          type="button"
          size="sm"
          className="h-7 flex-1 text-xs"
          disabled={sending}
          onClick={() => decide("approve")}
        >
          {t("messages.approval.allow")}
        </Button>
        <Button
          type="button"
          size="sm"
          variant="outline"
          className="h-7 flex-1 text-xs"
          disabled={sending}
          onClick={() => decide("approve_all")}
        >
          {t("messages.approval.allowAll")}
        </Button>
        <Button
          type="button"
          size="sm"
          variant="outline"
          className="h-7 flex-1 text-xs"
          disabled={sending}
          onClick={() => decide("reject")}
        >
          {t("messages.approval.deny")}
        </Button>
      </div>
    </div>
  )
})
