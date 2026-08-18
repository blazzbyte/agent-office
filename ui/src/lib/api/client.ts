import type { ApiResponse } from "@/types/api"
import { loadConnectionConfig, buildUrl } from "./config"
import { safeParseResponse, createApiError, ApiError } from "./errors"

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------

interface ApiClientOptions {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE"
  body?: unknown
  signal?: AbortSignal
  /** Request timeout in milliseconds. Default: 10 000 */
  timeout?: number
  /**
   * Override the base URL for this request only.
   * Useful for health checks before the store config is applied.
   */
  baseUrl?: string
  /** Extra headers merged on top of the auth headers. */
  headers?: Record<string, string>
}

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------
export async function apiClient<T = unknown>(
  endpoint: string,
  options: ApiClientOptions = {},
): Promise<ApiResponse<T>> {
  const { method = "GET", body, signal, timeout = 10_000 } = options
  const config = loadConnectionConfig()

  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    Accept: "application/json",
    ...(options.headers ?? {}),
  }


  const url = buildUrl(options.baseUrl ?? config.baseUrl, endpoint)

  // Timeout via AbortController chaining
  const timeoutController = new AbortController()
  const timeoutId = setTimeout(() => timeoutController.abort("TIMEOUT"), timeout)

  // Chain an external signal if provided
  if (signal) {
    if (signal.aborted) {
      clearTimeout(timeoutId)
      timeoutController.abort(signal.reason)
    } else {
      signal.addEventListener("abort", () =>
        timeoutController.abort(signal.reason),
      )
    }
  }

  try {
    const response = await fetch(url, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal: timeoutController.signal,
    })

    clearTimeout(timeoutId)

    const parsedResponse = await safeParseResponse(response)
    const data = (
      parsedResponse.isJson ? parsedResponse.json : parsedResponse.text
    ) as T

    if (!response.ok) {
      return { data, parsedResponse, response, ok: false }
    }

    return { data, parsedResponse, response, ok: true }
  } catch (error) {
    clearTimeout(timeoutId)

    const isTimeout =
      error instanceof Error &&
      (error.name === "AbortError" || timeoutController.signal.aborted)

    const message = isTimeout
      ? "Request timed out. Is the backend running?"
      : error instanceof Error
        ? error.message
        : "Network or connection error"

    const syntheticResponse = new Response(
      JSON.stringify({ message, error: isTimeout ? "timeout" : "network_error" }),
      {
        status: isTimeout ? 408 : 503,
        statusText: isTimeout ? "Request Timeout" : "Service Unavailable",
        headers: { "Content-Type": "application/json" },
      },
    )

    const parsedResponse = await safeParseResponse(syntheticResponse)
    const apiError = createApiError(syntheticResponse, parsedResponse)

    return {
      data: apiError as unknown as T,
      parsedResponse,
      response: syntheticResponse,
      ok: false,
    }
  }
}

// Re-export so consumers can `import { apiClient, ApiError } from "@/lib/api/client"`.
export { ApiError }
