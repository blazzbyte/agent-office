
import { logger } from "@/lib/utils/logger"
import type { BridgeHealth } from "@/types/bridge"
import type { ExcelAction, StepResult } from "@/types/excel-actions"
import { translate } from "@/i18n"

const HEALTH_URL = "/bridge/v1/health"
const TOKEN_URL = "/bridge/v1/token"
const EXECUTE_URL = "/bridge/v1/execute"

const PROBE_TIMEOUT_MS = 3_000
/** Slightly above the bridge's own 60s action timeout. */
const EXECUTE_TIMEOUT_MS = 65_000

// ---------------------------------------------------------------------------
// Bridge auth token
// ---------------------------------------------------------------------------
//
// Kept for protocol compatibility: the host answers `GET /bridge/v1/token`
// and may (but need not) enforce `X-AgentOffice-Bridge-Token` on /execute — both
// sides live in the same process now, so there is no trust boundary. The
// client tolerates a missing token endpoint just like before.

let cachedToken: string | null = null
/** Log the "no token available" hint only once per session. */
let tokenUnavailableLogged = false

/**
 * Fetch the bridge token (same-origin via Caddy). Returns `null` when the
 * bridge is down or older than v1.0.0 — transient, retried on next execute.
 */
async function fetchBridgeToken(): Promise<string | null> {
  try {
    const res = await fetch(TOKEN_URL, { headers: { Accept: "application/json" } })
    if (!res.ok) {
      if (!tokenUnavailableLogged) {
        tokenUnavailableLogged = true
        logger.info("No bridge token endpoint (bridge down or older than v1.0.0)", {
          status: res.status,
        })
      }
      return null
    }
    const data = (await res.json()) as { token?: string }
    return typeof data?.token === "string" && data.token.length > 0 ? data.token : null
  } catch {
    return null
  }
}

/** Cached bridge token; fetches on first use. `forceRefresh` re-asks the gateway. */
async function getBridgeToken(forceRefresh = false): Promise<string | null> {
  if (cachedToken && !forceRefresh) return cachedToken
  cachedToken = await fetchBridgeToken()
  return cachedToken
}

function bridgeAuthHeaders(token: string | null): Record<string, string> {
  return token ? { "X-AgentOffice-Bridge-Token": token } : {}
}

// ---------------------------------------------------------------------------
// Probe
// ---------------------------------------------------------------------------
export async function probeBridge(timeoutMs: number = PROBE_TIMEOUT_MS): Promise<BridgeHealth | null> {
  const ctrl = new AbortController()
  const timer = setTimeout(() => ctrl.abort(), timeoutMs)
  try {
    const res = await fetch(HEALTH_URL, {
      signal: ctrl.signal,
      headers: { Accept: "application/json" },
    })
    if (!res.ok) return null

    const data = (await res.json()) as Partial<BridgeHealth>
    // Guard against non-bridge responses (e.g. the Vite SPA fallback HTML-JSON)
    if (data?.name !== "agent-office" || !Array.isArray(data.actions)) {
      return null
    }
    return data as BridgeHealth
  } catch {
    return null
  } finally {
    clearTimeout(timer)
  }
}

// ---------------------------------------------------------------------------
// Workbook hint (multi-process safety, see plan §10.2)
// ---------------------------------------------------------------------------
function getWorkbookHint(): string | null {
  try {
    const name = window.__agentOfficeHost?.workbook
    return typeof name === "string" && name.length > 0 ? name : null
  } catch {
    return null
  }
}

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

/**
 * Forward an action to the native bridge (in-proc VSTO executor) for COM-level execution.
 * Always resolves to a StepResult — never throws.
 */
export async function executeViaBridge(action: ExcelAction): Promise<StepResult> {
  const workbookHint = getWorkbookHint()

  const post = async (token: string | null): Promise<Response> => {
    const ctrl = new AbortController()
    const timer = setTimeout(() => ctrl.abort(), EXECUTE_TIMEOUT_MS)
    try {
      return await fetch(EXECUTE_URL, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Accept: "application/json",
          ...bridgeAuthHeaders(token),
        },
        body: JSON.stringify({
          id: action.id,
          action: action.action,
          params: action.params ?? {},
          ...(workbookHint ? { workbookHint } : {}),
        }),
        signal: ctrl.signal,
      })
    } finally {
      clearTimeout(timer)
    }
  }

  try {
    let res = await post(await getBridgeToken())

    // 401 = stale/absent token (e.g. Excel restarted, token rotated) —
    // re-fetch once from the gateway and retry before giving up.
    if (res.status === 401) {
      logger.info("Bridge rejected token — refreshing and retrying once")
      res = await post(await getBridgeToken(true))
    }

    const data = (await res.json().catch(() => null)) as StepResult | null

    if (res.status === 401) {
      return {
        id: action.id,
        status: "error",
        summary: translate("system.bridgeAuthRejected"),
      }
    }

    if (!res.ok || !data || typeof data.summary !== "string") {
      return {
        id: action.id,
        status: "error",
        summary: translate("system.bridgeRequestFailed", { status: res.status }),
      }
    }

    return {
      id: action.id,
      status: data.status === "success" ? "success" : "error",
      summary: data.summary,
      data: data.data,
    }
  } catch (err) {
    logger.warn("executeViaBridge: bridge unreachable", { err })
    return {
      id: action.id,
      status: "error",
      summary: translate("system.bridgeUnreachable"),
    }
  }
}
