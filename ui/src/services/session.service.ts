
import { apiClient } from "@/lib/api/client"
import { handleApiError } from "@/lib/api/errors"
import { logger } from "@/lib/utils/logger"
import {
  healthResponseSchema,
  sessionListResponseSchema,
  sessionMessagesResponseSchema,
  sessionResponseSchema,
  sessionDeletedResponseSchema,
} from "@/schemas/session.schema"
import type {
  CreateSessionBody,
  HealthResponse,
  SessionListResponse,
} from "@/types/api"
import type { AgentSession, AgentSessionMessage, ProviderModelInfo, ProviderSettings } from "@/types/session"

// ---------------------------------------------------------------------------
// Provider settings (host-owned: settings.json via the in-proc agent)
// ---------------------------------------------------------------------------
export async function getProviderModels(
  signal?: AbortSignal,
): Promise<ProviderModelInfo[] | null> {
  const { data, ok } = await apiClient<{ data?: ProviderModelInfo[] }>(
    "/api/settings/provider/models",
    { signal },
  )
  if (!ok || !Array.isArray(data?.data)) {
    logger.warn("getProviderModels: request failed", { ok })
    return null
  }
  return data.data
}

/**
 * Read the LLM provider settings from the host.
 * Returns `null` when the agent is unreachable.
 */
export async function getProviderSettings(): Promise<ProviderSettings | null> {
  const { data, ok } = await apiClient<{ provider?: ProviderSettings }>(
    "/api/settings/provider",
  )
  if (!ok || !data?.provider) {
    logger.warn("getProviderSettings: request failed", { ok })
    return null
  }
  return data.provider
}

/**
 * Persist a partial provider-settings update on the host.
 * Returns the fresh settings, or `null` on failure.
 */
export async function saveProviderSettings(patch: {
  apiKey?: string
  baseUrl?: string
  model?: string
  contextWindow?: number
  maxTokens?: number
  maxSteps?: number
  imageModel?: string
  imageBaseUrl?: string
  imageApiKey?: string
  imageProvider?: string
}, signal?: AbortSignal): Promise<ProviderSettings | null> {
  const { data, ok } = await apiClient<{ provider?: ProviderSettings }>(
    "/api/settings/provider",
    { method: "PUT", body: patch, signal },
  )
  if (!ok || !data?.provider) {
    logger.warn("saveProviderSettings: request failed", { ok })
    return null
  }
  return data.provider
}

// ---------------------------------------------------------------------------
// Health check
// ---------------------------------------------------------------------------

/**
 * Ping the gateway's `/health` endpoint.
 * Returns the health payload on success, `null` if unreachable.
 */
export async function checkHealth(): Promise<HealthResponse | null> {
  const { data, ok } = await apiClient<HealthResponse>("/health")
  if (!ok) {
    logger.warn("Health check failed", { data })
    return null
  }
  const parsed = healthResponseSchema.safeParse(data)
  if (!parsed.success) {
    logger.warn("Health check response shape invalid", {
      errors: parsed.error.format(),
    })
    return null
  }
  return parsed.data
}

// ---------------------------------------------------------------------------
// MCP detection
// ---------------------------------------------------------------------------

/**
 * Query `/v1/toolsets` to check if `excel-mcp` (`mcp_excel` / `mcp__excel_mcp__*`)
 * is registered and active on the connected agent backend.
 */
export async function checkExcelMcpInstalled(): Promise<boolean> {
  try {
    const { data, ok } = await apiClient<{ data?: Array<{ name?: string; tools?: string[] }> }>("/v1/toolsets")
    if (ok && data?.data && Array.isArray(data.data)) {
      const hasExcel = data.data.some(
        (ts) =>
          ts.name?.toLowerCase().includes("excel") ||
          ts.tools?.some((t) => t.toLowerCase().includes("excel"))
      )
      if (hasExcel) return true
    }
  } catch (error) {
    logger.debug("checkExcelMcpInstalled: error querying toolsets", { error })
  }
  return false
}

// ---------------------------------------------------------------------------
// Session CRUD
// ---------------------------------------------------------------------------

/**
 * List all persisted sessions visible to this API key.
 */
export async function listSessions(options?: {
  limit?: number
  offset?: number
  source?: string
}): Promise<SessionListResponse | null> {
  const params = new URLSearchParams()
  if (options?.limit !== undefined) params.set("limit", String(options.limit))
  if (options?.offset !== undefined) params.set("offset", String(options.offset))
  if (options?.source) params.set("source", options.source)

  const query = params.size > 0 ? `?${params.toString()}` : ""
  const { data, ok } = await apiClient<SessionListResponse>(`/api/sessions${query}`)

  if (!ok) {
    handleApiError(data, {}, "listSessions: request failed")
    return null
  }

  const parsed = sessionListResponseSchema.safeParse(data)
  if (!parsed.success) {
    logger.warn("listSessions: unexpected response shape", { issues: parsed.error.issues })
    return null
  }

  return parsed.data as SessionListResponse
}

/**
 * Create a new empty session on the gateway.
 * Returns the created session or `null` on failure.
 */
export async function createSession(
  body?: CreateSessionBody,
): Promise<AgentSession | null> {
  const { data, ok } = await apiClient<{ session: AgentSession }>(
    "/api/sessions",
    { method: "POST", body: body ?? {} },
  )

  if (!ok) {
    handleApiError(data, {}, "createSession: request failed")
    return null
  }

  const parsed = sessionResponseSchema.safeParse(data)
  if (!parsed.success) {
    logger.warn("createSession: unexpected response shape", { issues: parsed.error.issues })
    return null
  }

  return parsed.data.session as AgentSession
}

/**
 * Fetch a single session by ID.
 * Returns `null` for both 404 (expected) and unexpected errors.
 */
export async function getSession(sessionId: string): Promise<AgentSession | null> {
  const { data, ok, response } = await apiClient<unknown>(`/api/sessions/${sessionId}`)

  if (!ok) {
    if (response.status === 404) {
      return null // Session no longer exists — not an error
    }
    handleApiError(data, { sessionId }, "getSession: request failed")
    return null
  }

  const parsed = sessionResponseSchema.safeParse(data)
  if (!parsed.success) {
    logger.warn("getSession: unexpected response shape", { issues: parsed.error.issues })
    return null
  }

  return parsed.data.session as AgentSession
}

/**
 * Retrieve the full message history for a session.
 * Returns an empty array on any failure.
 */
export async function getSessionMessages(
  sessionId: string,
): Promise<AgentSessionMessage[]> {
  const { data, ok } = await apiClient<unknown>(
    `/api/sessions/${sessionId}/messages`,
  )

  if (!ok) {
    handleApiError(data, { sessionId }, "getSessionMessages: request failed")
    return []
  }

  const parsed = sessionMessagesResponseSchema.safeParse(data)
  if (!parsed.success) {
    logger.warn("getSessionMessages: unexpected response shape", {
      issues: parsed.error.issues,
    })
    return []
  }

  return parsed.data.data as AgentSessionMessage[]
}

/**
 * Delete a session from the gateway.
 * Returns `true` if the session was successfully deleted.
 */
export async function deleteSession(sessionId: string): Promise<boolean> {
  const { data, ok } = await apiClient<unknown>(
    `/api/sessions/${sessionId}`,
    { method: "DELETE" },
  )

  if (!ok) {
    handleApiError(data, { sessionId }, "deleteSession: request failed")
    return false
  }

  const parsed = sessionDeletedResponseSchema.safeParse(data)
  return parsed.success ? parsed.data.deleted : false
}
