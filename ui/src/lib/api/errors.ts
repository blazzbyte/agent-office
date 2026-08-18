import type { ParsedResponseData } from "@/types/api"
import { logger } from "@/lib/utils/logger"

// ---------------------------------------------------------------------------
// ApiError
// ---------------------------------------------------------------------------

interface V8ErrorConstructor {
  captureStackTrace?: (target: object, ctor?: { name: string }) => void
}

/**
 * Thrown by `apiClient` when the gateway returns a non-2xx status,
 * or when the network request itself fails (timeout, DNS error, etc.).
 */
export class ApiError extends Error {
  public readonly status: number
  public readonly statusText: string
  public readonly responseData: ParsedResponseData
  public readonly url: string

  constructor(
    message: string,
    status: number,
    statusText: string,
    responseData: ParsedResponseData,
    url: string,
  ) {
    super(message)
    this.name = "ApiError"
    this.status = status
    this.statusText = statusText
    this.responseData = responseData
    this.url = url

    const v8 = Error as unknown as V8ErrorConstructor
    if (v8.captureStackTrace) {
      v8.captureStackTrace(this, ApiError)
    }
  }

  toLogObject(): Record<string, unknown> {
    return {
      name: this.name,
      message: this.message,
      status: this.status,
      statusText: this.statusText,
      url: this.url,
      responseData: this.responseData.isJson
        ? this.responseData.json
        : this.responseData.text,
    }
  }
}

// ---------------------------------------------------------------------------
// Response parsing
// ---------------------------------------------------------------------------

/**
 * Safely consume the response body and attempt JSON parsing.
 * Always returns without throwing — callers decide how to handle bad JSON.
 */
export async function safeParseResponse(response: Response): Promise<ParsedResponseData> {
  const text = await response.text().catch(() => "")
  if (!text || text.trim() === "") {
    return { isJson: false, text: "" }
  }
  try {
    const json: unknown = JSON.parse(text)
    return { isJson: true, json }
  } catch {
    return { isJson: false, text }
  }
}

/**
 * Build an `ApiError` from a failed `Response` and its already-parsed body.
 */
export function createApiError(
  response: Response,
  parsedData: ParsedResponseData,
): ApiError {
  // Try to extract a human-readable message from the gateway's OpenAI-style
  // error envelope: `{ "error": { "message": "..." } }`
  let message = `HTTP ${response.status}: ${response.statusText}`
  if (parsedData.isJson && typeof parsedData.json === "object" && parsedData.json !== null) {
    const envelope = parsedData.json as Record<string, unknown>
    const error = envelope["error"]
    if (typeof error === "object" && error !== null) {
      const apiMsg = (error as Record<string, unknown>)["message"]
      if (typeof apiMsg === "string" && apiMsg) {
        message = apiMsg
      }
    } else if (typeof envelope["message"] === "string") {
      message = envelope["message"]
    }
  }

  return new ApiError(message, response.status, response.statusText, parsedData, response.url)
}

// ---------------------------------------------------------------------------
// Error logging helpers
// ---------------------------------------------------------------------------

interface ErrorContext {
  [key: string]: unknown
}

/**
 * Log and handle any error thrown by an API call.
 * Handles both `ApiError` instances and generic `Error`s.
 */
export function handleApiError(
  error: unknown,
  context: ErrorContext,
  internalMessage: string,
): void {
  if (error instanceof ApiError) {
    logger.error(internalMessage, { ...error.toLogObject(), ...context })
    return
  }
  if (error instanceof Error) {
    logger.error(internalMessage, {
      error: { name: error.name, message: error.message },
      ...context,
    })
    return
  }
  logger.error(internalMessage, { error, ...context })
}

/**
 * Structured log for a failed HTTP response (without throwing).
 */
export function logApiError(
  message: string,
  status: number,
  statusText: string,
  responseData: ParsedResponseData,
  context: ErrorContext = {},
): void {
  logger.error(message, {
    status,
    statusText,
    responseData: responseData.isJson ? responseData.json : responseData.text,
    ...context,
  })
}
