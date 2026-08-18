import * as z from "zod"

// ---------------------------------------------------------------------------
// Connection config schema
// ---------------------------------------------------------------------------

export const connectionConfigSchema = z.object({
  baseUrl: z
    .string()
    .url("Must be a valid URL")
    .default("https://app.agentoffice.local"),
  sessionKey: z.string().min(1, "Session key cannot be empty"),
})

export type ConnectionConfigInput = z.input<typeof connectionConfigSchema>
export type ConnectionConfigOutput = z.output<typeof connectionConfigSchema>
