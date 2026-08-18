
interface LogContext {
  [key: string]: unknown
}

/**
 * Formats a log entry with timestamp (dev only), level tag, message, and context.
 */
const formatLog = (level: string, message: string, context?: LogContext): string => {
  const timestamp = import.meta.env.DEV ? `[${new Date().toISOString()}] ` : ""
  const prefix = `${timestamp}[agent-office] ${level}:`

  if (context && Object.keys(context).length > 0) {
    return `${prefix} ${message} | ${JSON.stringify(context, null, 0)}`
  }

  return `${prefix} ${message}`
}
export const logger = {
  /**
   * Informational messages about normal application flow
   */
  info: (message: string, context?: LogContext): void => {
    console.log(formatLog("INFO", message, context))
  },

  /**
   * Error messages — always logged, even in production
   */
  error: (message: string, context?: LogContext): void => {
    console.error(formatLog("ERROR", message, context))
  },

  /**
   * Debug messages — only emitted in development builds (import.meta.env.DEV)
   */
  debug: (message: string, context?: LogContext): void => {
    if (import.meta.env.DEV) {
      console.log(formatLog("DEBUG", message, context))
    }
  },

  /**
   * Warning messages — always logged
   */
  warn: (message: string, context?: LogContext): void => {
    console.warn(formatLog("WARN", message, context))
  },
}
