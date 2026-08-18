import type { AgentConnectionConfig } from "@/types/session"
import { logger } from "@/lib/utils/logger"

const CONFIG_STORAGE_KEY = "agent-office:connection-config"
const SESSION_KEY_STORAGE = "agent-office:session-key"

// ---------------------------------------------------------------------------
// Defaults
// ---------------------------------------------------------------------------

export const DEFAULT_CONNECTION_CONFIG: Readonly<AgentConnectionConfig> = {
  baseUrl:
    typeof window !== "undefined" && window.location.protocol === "https:"
      ? window.location.origin
      : "http://localhost:5173",
  sessionKey: "",
} as const

// ---------------------------------------------------------------------------
// Session key
// ---------------------------------------------------------------------------
function getOrCreateSessionKey(): string {
  try {
    const stored = localStorage.getItem(SESSION_KEY_STORAGE)
    if (stored) return stored

    const newKey = crypto.randomUUID()
    localStorage.setItem(SESSION_KEY_STORAGE, newKey)
    return newKey
  } catch (error) {
    logger.warn("Could not read/write localStorage for session key", { error })
    // Fall back to a runtime-only key — won't persist across reloads.
    return crypto.randomUUID()
  }
}

// ---------------------------------------------------------------------------
// Load / Save
// ---------------------------------------------------------------------------

/** `true` when hosted by the native add-in (HTTPS virtual origin). */
function isNativeHost(): boolean {
  return typeof window !== "undefined" && window.location.protocol === "https:"
}
export function loadConnectionConfig(): AgentConnectionConfig {
  try {
    const raw = localStorage.getItem(CONFIG_STORAGE_KEY)
    if (!raw) {
      return { ...DEFAULT_CONNECTION_CONFIG, sessionKey: getOrCreateSessionKey() }
    }

    const parsed = JSON.parse(raw) as Partial<AgentConnectionConfig>
    const baseUrl = isNativeHost()
      ? window.location.origin
      : parsed.baseUrl?.trim() || DEFAULT_CONNECTION_CONFIG.baseUrl

    return {
      baseUrl,
      sessionKey: parsed.sessionKey || getOrCreateSessionKey(),
    }
  } catch (error) {
    logger.warn("Failed to load connection config from localStorage", { error })
    return { ...DEFAULT_CONNECTION_CONFIG, sessionKey: getOrCreateSessionKey() }
  }
}


/**
 * Persist the connection config to `localStorage`.
 */
export function saveConnectionConfig(config: AgentConnectionConfig): void {
  try {
    localStorage.setItem(CONFIG_STORAGE_KEY, JSON.stringify(config))
  } catch (error) {
    logger.warn("Failed to save connection config to localStorage", { error })
  }
}

// ---------------------------------------------------------------------------
// URL helpers
// ---------------------------------------------------------------------------
export function buildUrl(baseUrl: string, path: string): string {
  const base = baseUrl.endsWith("/") ? baseUrl.slice(0, -1) : baseUrl
  const endpoint = path.startsWith("/") ? path : `/${path}`
  return `${base}${endpoint}`
}
