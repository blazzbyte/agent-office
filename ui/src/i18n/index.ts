import { create } from "zustand"

import { apiClient } from "@/lib/api"
import { logger } from "@/lib/utils/logger"
import { common } from "./messages/common"
import { messages } from "./messages/messages"
import { settings } from "./messages/settings"
import { shell } from "./messages/shell"
import { system } from "./messages/system"

// ---------------------------------------------------------------------------
// i18n — built-in English/Spanish dictionaries plus optional overrides from
// settings.json ("language", "translations"); unknown keys fall back to English.
// ---------------------------------------------------------------------------

// Register each area's messages here.
const areas = [common, settings, messages, shell, system] as const

type Area = (typeof areas)[number]
type UnionToIntersection<U> = (U extends unknown ? (x: U) => void : never) extends (
  x: infer I,
) => void
  ? I
  : never

export type MessageKey = keyof UnionToIntersection<Area["en"]> & string
export type Language = "en" | "es"
export const LANGUAGES: readonly Language[] = ["en", "es"]

const dictionaries: Record<Language, Record<string, string>> = {
  en: Object.assign({}, ...areas.map((a) => a.en)),
  es: Object.assign({}, ...areas.map((a) => a.es)),
}

/** "es-ES" → "es"; anything unsupported → null. */
function normalize(code: string | null | undefined): Language | null {
  const base = (code ?? "").trim().toLowerCase().split(/[-_]/)[0]
  return (LANGUAGES as readonly string[]).includes(base) ? (base as Language) : null
}

function detectBrowserLanguage(): Language {
  return normalize(typeof navigator !== "undefined" ? navigator.language : null) ?? "en"
}

interface I18nState {
  /** Explicit choice from settings.json; null = follow Office/Windows. */
  preference: Language | null
  language: Language
  overrides: Record<string, string>
  load: () => Promise<void>
  setPreference: (preference: Language | null) => Promise<void>
}

interface UiSettings {
  language?: string | null
  translations?: Record<string, unknown> | null
}

function applyDocumentLanguage(language: Language): void {
  if (typeof document !== "undefined") document.documentElement.lang = language
}

export const useI18nStore = create<I18nState>((set) => ({
  preference: null,
  language: detectBrowserLanguage(),
  overrides: {},

  load: async () => {
    const { data, ok } = await apiClient<{ ui?: UiSettings }>("/api/settings/ui")
    if (!ok || !data?.ui) {
      logger.warn("i18n: could not read UI settings — using browser language")
      return
    }
    const preference = normalize(data.ui.language)
    const overrides: Record<string, string> = {}
    for (const [key, value] of Object.entries(data.ui.translations ?? {})) {
      if (typeof value === "string" && value.trim()) overrides[key] = value
    }
    const language = preference ?? detectBrowserLanguage()
    applyDocumentLanguage(language)
    set({ preference, language, overrides })
  },

  setPreference: async (preference) => {
    const language = preference ?? detectBrowserLanguage()
    applyDocumentLanguage(language)
    set({ preference, language })
    const { ok } = await apiClient("/api/settings/ui", {
      method: "PUT",
      body: { language: preference ?? "" },
    })
    if (!ok) logger.warn("i18n: could not persist language preference")
  },
}))

export type TranslateVars = Record<string, string | number>

function format(template: string, vars?: TranslateVars): string {
  if (!vars) return template
  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in vars ? String(vars[name]) : match,
  )
}

function lookup(
  language: Language,
  overrides: Record<string, string>,
  key: MessageKey,
  vars?: TranslateVars,
): string {
  const template = overrides[key] ?? dictionaries[language][key] ?? dictionaries.en[key] ?? key
  return format(template, vars)
}

/** Non-React translation (stores, services, toasts). */
export function translate(key: MessageKey, vars?: TranslateVars): string {
  const { language, overrides } = useI18nStore.getState()
  return lookup(language, overrides, key, vars)
}

/** React hook: `const t = useT(); t("settings.save")` — re-renders on language change. */
export function useT(): (key: MessageKey, vars?: TranslateVars) => string {
  const language = useI18nStore((s) => s.language)
  const overrides = useI18nStore((s) => s.overrides)
  return (key, vars) => lookup(language, overrides, key, vars)
}

/** BCP-47 locale for Intl date/number formatting. */
export function useLocale(): string {
  return useI18nStore((s) => s.language)
}
