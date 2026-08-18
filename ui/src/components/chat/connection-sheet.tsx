import { memo, useEffect, useMemo, useRef, useState } from "react"
import { Check, ChevronsUpDown, Pencil } from "lucide-react"
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { PasswordInput } from "@/components/ui/password-input"
import { Label } from "@/components/ui/label"
import { cn } from "@/lib/utils"
import { useConnection } from "@/hooks/use-connection"
import { useUiStore, selectRenderStyle } from "@/store/ui.store"
import {
  getProviderModels,
  getProviderSettings,
  saveProviderSettings,
} from "@/services/session.service"
import { logger } from "@/lib/utils/logger"
import type { ProviderModelInfo } from "@/types/session"

// ---------------------------------------------------------------------------
// ConnectionSheet — LLM provider settings (API key + base URL + model).
// ---------------------------------------------------------------------------

/** "128K context" — opencode Locale.number style. */
function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${Math.round(n / 1_000)}K`
  return String(n)
}

/** Per-1M-token price, trimmed for the picker list. */
function formatPrice(usdPerMillion: number): string {
  if (usdPerMillion <= 0) return "free"
  if (usdPerMillion < 1) return `$${usdPerMillion.toFixed(2)}`
  return `$${usdPerMillion.toFixed(1)}`
}

/** Compact modality chips: " · img/vid/pdf(+out:img)" — empty when text-only. */
function modalityBadges(m: {
  modalities_input?: string[]
  modalities_output?: string[]
}): string {
  const short: Record<string, string> = { image: "img", video: "vid", audio: "aud", pdf: "pdf" }
  const chips = (m.modalities_input ?? []).map((mod) => short[mod] ?? mod)
  if (m.modalities_output?.includes("image")) chips.push("out:img")
  if (m.modalities_output?.includes("audio")) chips.push("out:aud")
  return chips.length > 0 ? " · " + chips.join("/") : ""
}

// ---------------------------------------------------------------------------
// Provider presets — canonical base URLs for known OpenAI-compatible endpoints.
// ---------------------------------------------------------------------------

const CUSTOM_ID = "custom"

const PROVIDER_PRESETS: ReadonlyArray<{ id: string; label: string; baseUrl: string }> = [
  { id: "openai", label: "OpenAI", baseUrl: "https://api.openai.com/v1" },
  { id: "novita", label: "Novita AI", baseUrl: "https://api.novita.ai/v3/openai" },
  { id: "deepseek", label: "DeepSeek", baseUrl: "https://api.deepseek.com/v1" },
  { id: "openrouter", label: "OpenRouter", baseUrl: "https://openrouter.ai/api/v1" },
  { id: "groq", label: "Groq", baseUrl: "https://api.groq.com/openai/v1" },
  { id: "together", label: "Together AI", baseUrl: "https://api.together.xyz/v1" },
  { id: "mistral", label: "Mistral", baseUrl: "https://api.mistral.ai/v1" },
  { id: "xai", label: "xAI", baseUrl: "https://api.x.ai/v1" },
  { id: "gemini", label: "Google Gemini", baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai" },
]

function presetForUrl(url: string): string {
  const trimmed = url.trim().replace(/\/+$/, "").toLowerCase()
  return (
    PROVIDER_PRESETS.find((p) => p.baseUrl.toLowerCase() === trimmed)?.id ??
    CUSTOM_ID
  )
}

// ---------------------------------------------------------------------------
// Image generation presets — the generate_image tool's provider, independent
// from chat. Three wire shapes host-side: "openai-images" (the de-facto
// standard: OpenAI / OpenRouter / Together / custom), "gemini" (native
// generateContent), "a1111" (local Automatic1111 / SD.Next — no key/model).
// ---------------------------------------------------------------------------

const IMAGE_OFF = "off"
const IMAGE_CUSTOM = "custom"

interface ImagePreset {
  id: string
  label: string
  provider: string
  baseUrl: string
  model: string
  needsKey: boolean
  needsModel: boolean
  editableUrl: boolean
}

const IMAGE_PRESETS: ReadonlyArray<ImagePreset> = [
  {
    id: "openai",
    label: "OpenAI (gpt-image)",
    provider: "openai-images",
    baseUrl: "https://api.openai.com/v1",
    model: "gpt-image-1",
    needsKey: true,
    needsModel: true,
    editableUrl: false,
  },
  {
    id: "openrouter",
    label: "OpenRouter (FLUX, nano banana…)",
    provider: "openai-images",
    baseUrl: "https://openrouter.ai/api/v1",
    model: "google/gemini-2.5-flash-image",
    needsKey: true,
    needsModel: true,
    editableUrl: false,
  },
  {
    id: "gemini",
    label: "Google Gemini (nano banana)",
    provider: "gemini",
    baseUrl: "https://generativelanguage.googleapis.com/v1beta",
    model: "gemini-2.5-flash-image",
    needsKey: true,
    needsModel: true,
    editableUrl: false,
  },
  {
    id: "a1111",
    label: "Local — A1111 / SD.Next",
    provider: "a1111",
    baseUrl: "http://localhost:7860",
    model: "",
    needsKey: false,
    needsModel: false,
    editableUrl: true,
  },
]

/** Infer the image preset from saved host settings (best effort). */
function inferImagePreset(s: {
  imageModel: string
  imageBaseUrl: string
  imageProvider?: string
}): string {
  if (!s.imageModel && !s.imageBaseUrl) return IMAGE_OFF
  const url = s.imageBaseUrl.trim().replace(/\/+$/, "").toLowerCase()
  const match = IMAGE_PRESETS.find(
    (p) =>
      p.provider === s.imageProvider && p.baseUrl.toLowerCase() === url,
  )
  if (match) return match.id
  if (s.imageProvider === "a1111") return "a1111" // non-default local port
  return IMAGE_CUSTOM
}

interface ConnectionSheetProps {
  open: boolean
  onOpenChange: (open: boolean) => void
}

export const ConnectionSheet = memo(function ConnectionSheet({
  open,
  onOpenChange,
}: ConnectionSheetProps): React.JSX.Element {
  const { checkHealth } = useConnection()
  const renderStyle = useUiStore(selectRenderStyle)
  const setRenderStyle = useUiStore((s) => s.setRenderStyle)
  const [presetId, setPresetId] = useState(CUSTOM_ID)
  const [apiKey, setApiKey] = useState("")
  const [baseUrl, setBaseUrl] = useState("")
  const [model, setModel] = useState("")
  const [imagePreset, setImagePreset] = useState(IMAGE_OFF)
  const [imageModel, setImageModel] = useState("")
  const [imageBaseUrl, setImageBaseUrl] = useState("")
  const [imageApiKey, setImageApiKey] = useState("")
  const [isLoading, setIsLoading] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [models, setModels] = useState<ProviderModelInfo[] | null>(null)
  const [modelsLoading, setModelsLoading] = useState(false)
  const [modelsAttempted, setModelsAttempted] = useState(false)
  const [manualModel, setManualModel] = useState(false)
  const [comboOpen, setComboOpen] = useState(false)
  const [search, setSearch] = useState("")

  const selectModel = (id: string): void => {
    setModel(id)
    setComboOpen(false)
    setSearch("")
  }

  const applyPreset = (id: string): void => {
    setPresetId(id)
    const preset = PROVIDER_PRESETS.find((p) => p.id === id)
    if (!preset) return // custom: keep whatever URL is in the box
    setBaseUrl(preset.baseUrl)
    // Model ids and catalogs are provider-specific — reset both.
    setModel("")
    setModels(null)
    setModelsAttempted(false)
    setManualModel(false)
  }

  const applyImagePreset = (id: string): void => {
    setImagePreset(id)
    const preset = IMAGE_PRESETS.find((p) => p.id === id)
    if (!preset) return // off/custom: keep whatever is in the boxes
    setImageBaseUrl(preset.baseUrl)
    // Prefill the suggested model unless the user typed a custom one.
    if (
      preset.needsModel &&
      (!imageModel || IMAGE_PRESETS.some((p) => p.model === imageModel))
    ) {
      setImageModel(preset.model)
    }
  }

  const imagePresetDef = IMAGE_PRESETS.find((p) => p.id === imagePreset) ?? null
  const imageNeedsModel =
    imagePreset === IMAGE_CUSTOM || (imagePresetDef?.needsModel ?? false)
  const imageNeedsKey =
    imagePreset === IMAGE_CUSTOM || (imagePresetDef?.needsKey ?? false)

  const imagePayload = (): {
    imageModel: string
    imageBaseUrl: string
    imageApiKey: string
    imageProvider: string
  } => {
    if (imagePreset === IMAGE_OFF) {
      return { imageModel: "", imageBaseUrl: "", imageApiKey: "", imageProvider: "" }
    }
    const editable =
      imagePreset === IMAGE_CUSTOM || (imagePresetDef?.editableUrl ?? false)
    return {
      imageModel: imageNeedsModel ? imageModel.trim() : "",
      imageBaseUrl: editable ? imageBaseUrl.trim() : (imagePresetDef?.baseUrl ?? ""),
      imageApiKey: imageApiKey.trim(),
      imageProvider: imagePresetDef?.provider ?? "",
    }
  }

  const catalogReady = (models?.length ?? 0) > 0
  const catalogFailed =
    modelsAttempted && !modelsLoading && (models === null || models.length === 0)
  const showModel = catalogReady || manualModel || model.trim().length > 0

  const loadModels = (signal?: AbortSignal): void => {
    setModelsLoading(true)
    void getProviderModels(signal)
      .then((list) => {
        if (!signal?.aborted) setModels(list)
      })
      .catch((error) => logger.warn("ConnectionSheet: failed to load models", { error }))
      .finally(() => {
        if (!signal?.aborted) {
          setModelsLoading(false)
          setModelsAttempted(true)
        }
      })
  }

  // -------------------------------------------------------------------------
  // Auto-connect: editing the API key or switching provider debounces (600 ms)
  // into save → catalog load → health, so the model combobox appears by
  // itself — no Save round-trip. Every cycle aborts the previous one.
  // -------------------------------------------------------------------------
  const autoCtrl = useRef<AbortController | null>(null)
  useEffect(() => {
    if (!open || isLoading) return
    if (!apiKey.trim()) return
    const timer = setTimeout(() => {
      autoCtrl.current?.abort()
      const ctrl = new AbortController()
      autoCtrl.current = ctrl
      void saveProviderSettings(
        {
          apiKey: apiKey.trim(),
          baseUrl: baseUrl.trim(),
          model: model.trim(),
          ...imagePayload(),
        },
        ctrl.signal,
      ).then((saved) => {
        if (!saved || ctrl.signal.aborted) return
        loadModels(ctrl.signal)
        void checkHealth()
      })
    }, 600)
    return () => clearTimeout(timer)
    // Model intentionally excluded: picking one must not refetch the catalog.
  }, [apiKey, baseUrl, open, isLoading])

  // Pull the current host-side settings every time the sheet opens. The
  // auto-connect effect below then loads the catalog by itself.
  useEffect(() => {
    if (!open) return
    setSaveError(null)
    setSearch("")
    setIsLoading(true)
    void getProviderSettings()
      .then((settings) => {
        if (!settings) return
        setApiKey(settings.apiKey)
        setBaseUrl(settings.baseUrl)
        setModel(settings.model)
        setImagePreset(inferImagePreset(settings))
        setImageModel(settings.imageModel ?? "")
        setImageBaseUrl(settings.imageBaseUrl ?? "")
        setImageApiKey(settings.imageApiKey ?? "")
        setPresetId(presetForUrl(settings.baseUrl))
      })
      .catch((error) => logger.warn("ConnectionSheet: failed to load provider settings", { error }))
      .finally(() => setIsLoading(false))
  }, [open])

  const exactMatch = useMemo(() => {
    const q = search.trim().toLowerCase()
    if (!q || !models) return false
    return models.some((m) => m.id.toLowerCase() === q)
  }, [models, search])

  const selectedInfo = useMemo(
    () => models?.find((m) => m.id === model.trim()) ?? null,
    [models, model],
  )

  const handleSave = (): void => {
    setSaveError(null)
    void saveProviderSettings({
      apiKey: apiKey.trim(),
      baseUrl: baseUrl.trim(),
      model: model.trim(),
      ...imagePayload(),
    }).then((saved) => {
      if (!saved) {
        setSaveError("Could not save settings — is the add-in host running?")
        return
      }
      // Settings persist synchronously on the host — health reflects the new
      // values immediately.
      void checkHealth()
      if (!model.trim() || !catalogReady) {
        loadModels()
        return
      }
      onOpenChange(false)
    })
  }

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="right" className="w-72 p-4" aria-label="Connection settings">
        <SheetHeader className="p-0 pb-3">
          <SheetTitle className="text-sm">Provider</SheetTitle>
        </SheetHeader>

        <div className="flex flex-col gap-3">
          {/* 1. Provider preset */}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="provider-preset" className="text-xs">
              Provider
            </Label>
            <Select
              value={presetId}
              onValueChange={applyPreset}
              disabled={isLoading}
            >
              <SelectTrigger id="provider-preset" className="h-8 w-full text-xs">
                <SelectValue placeholder="Choose a provider…" />
              </SelectTrigger>
              <SelectContent position="popper">
                {PROVIDER_PRESETS.map((p) => (
                  <SelectItem key={p.id} value={p.id} className="text-xs">
                    {p.label}
                  </SelectItem>
                ))}
                <SelectItem value={CUSTOM_ID} className="text-xs">
                  Custom (OpenAI-compatible)…
                </SelectItem>
              </SelectContent>
            </Select>
          </div>

          {/* 2. API key */}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="provider-api-key" className="text-xs">
              API key
            </Label>
            <PasswordInput
              id="provider-api-key"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder="sk-…"
              spellCheck={false}
              autoComplete="off"
              disabled={isLoading}
              className="h-8 text-xs"
            />
          </div>

          {/* 3. Base URL — custom provider only */}
          {presetId === CUSTOM_ID && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-base-url" className="text-xs">
                Base URL
              </Label>
              <Input
                id="provider-base-url"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
                placeholder="https://api.openai.com/v1"
                spellCheck={false}
                autoComplete="off"
                disabled={isLoading}
                className="h-8 text-xs"
              />
            </div>
          )}

          {/* 4. Model — revealed once the connection has proven itself */}
          {!showModel && (
            <div className="rounded-md border border-dashed border-border px-2 py-2 text-[0.625rem] leading-snug text-muted-foreground">
              {modelsLoading
                ? "Connecting — loading the model catalog…"
                : "Save your key above — the model catalog loads once the connection succeeds."}
              {catalogFailed && !modelsLoading && (
                <>
                  {" "}
                  Couldn&apos;t load the catalog (check the key/provider).
                  {" "}
                  <button
                    type="button"
                    className="underline underline-offset-2 hover:text-foreground"
                    onClick={() => setManualModel(true)}
                  >
                    Enter the model id manually
                  </button>
                </>
              )}
            </div>
          )}

          {showModel && (
          <div className="flex flex-col gap-1.5">
            <div className="flex items-center justify-between">
              <Label htmlFor="provider-model" className="text-xs">
                Model
              </Label>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                className="h-5 px-1.5 text-[0.625rem] text-muted-foreground"
                onClick={() => loadModels()}
                disabled={modelsLoading || isLoading}
              >
                {modelsLoading ? "Loading…" : models ? "Reload models" : "Load models"}
              </Button>
            </div>

            {/* shadcn combobox: catalog browse + free-text id entry. */}
            <Popover open={comboOpen} onOpenChange={setComboOpen}>
              <PopoverTrigger asChild>
                <Button
                  id="provider-model"
                  type="button"
                  variant="outline"
                  role="combobox"
                  aria-expanded={comboOpen}
                  disabled={isLoading}
                  className="h-8 w-full justify-between px-2 text-xs font-normal"
                  title={selectedInfo?.name ?? model}
                >
                  <span className="truncate">
                    {model || "Select or type a model id…"}
                  </span>
                  <ChevronsUpDown className="ml-1 size-3.5 shrink-0 opacity-50" />
                </Button>
              </PopoverTrigger>
              <PopoverContent
                align="start"
                portalled={false}
                className="w-[var(--radix-popover-trigger-width)] p-0"
              >
                <Command>
                  <CommandInput
                    placeholder="Search or type a model id…"
                    value={search}
                    onValueChange={setSearch}
                    autoFocus
                    className="text-xs"
                  />
                  <CommandList>
                    <CommandEmpty>No catalog match.</CommandEmpty>
                    {models && models.length > 0 && (
                      <CommandGroup heading="Provider catalog">
                        {models.map((m) => (
                          <CommandItem
                            key={m.id}
                            value={m.id}
                            keywords={[m.name]}
                            onSelect={() => selectModel(m.id)}
                            className="text-xs"
                          >
                            <Check
                              className={
                                m.id === model.trim()
                                  ? "size-3.5 opacity-100"
                                  : "size-3.5 opacity-0"
                              }
                            />
                            <span className="min-w-0 flex-1 truncate">{m.id}</span>
                            <span className="shrink-0 text-[0.625rem] text-muted-foreground">
                              {m.context > 0 ? formatTokens(m.context) : ""}
                              {m.reasoning ? " · thinks" : ""}
                              {modalityBadges(m)}
                            </span>
                          </CommandItem>
                        ))}
                      </CommandGroup>
                    )}
                    {search.trim() && !exactMatch && (
                      <CommandGroup heading="Custom">
                        <CommandItem
                          value={search.trim()}
                          onSelect={() => selectModel(search.trim())}
                          className="text-xs"
                        >
                          <Pencil className="size-3.5 opacity-70" />
                          <span className="truncate">
                            Use &quot;{search.trim()}&quot; as-is
                          </span>
                        </CommandItem>
                      </CommandGroup>
                    )}
                  </CommandList>
                </Command>
              </PopoverContent>
            </Popover>

            {!models && !modelsLoading && (
              <p className="m-0 text-[0.625rem] leading-snug text-muted-foreground">
                Catalog unavailable (save first if you just edited the key/URL)
                — you can still type any model id.
              </p>
            )}

            {/* Metadata for the selected model (catalog-priced estimate). */}
            {selectedInfo && (
              <p className="m-0 text-[0.625rem] leading-snug text-muted-foreground">
                {selectedInfo.context > 0
                  ? `${formatTokens(selectedInfo.context)} context`
                  : "unknown context"}
                {selectedInfo.output > 0
                  ? ` · ${formatTokens(selectedInfo.output)} max out`
                  : ""}
                {selectedInfo.price_input > 0 || selectedInfo.price_output > 0
                  ? ` · ${formatPrice(selectedInfo.price_input)}/${formatPrice(selectedInfo.price_output)} per 1M`
                  : ""}
                {selectedInfo.reasoning ? " · reasoning" : ""}
                {modalityBadges(selectedInfo)}
                {!selectedInfo.vision &&
                !(selectedInfo.modalities_input ?? []).includes("image") &&
                selectedInfo.source !== "default"
                  ? " · text-only"
                  : ""}
              </p>
            )}
          </div>
          )}

          {/* 5. Image generation (optional) — powers the generate_image tool. */}
          {showModel && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="provider-image-preset" className="text-xs">
                Image generation{" "}
                <span className="text-muted-foreground">(optional)</span>
              </Label>
              <Select
                value={imagePreset}
                onValueChange={applyImagePreset}
                disabled={isLoading}
              >
                <SelectTrigger id="provider-image-preset" className="h-8 w-full text-xs">
                  <SelectValue placeholder="Choose an image provider…" />
                </SelectTrigger>
                <SelectContent position="popper">
                  <SelectItem value={IMAGE_OFF} className="text-xs">
                    Off
                  </SelectItem>
                  {IMAGE_PRESETS.map((p) => (
                    <SelectItem key={p.id} value={p.id} className="text-xs">
                      {p.label}
                    </SelectItem>
                  ))}
                  <SelectItem value={IMAGE_CUSTOM} className="text-xs">
                    Custom
                  </SelectItem>
                </SelectContent>
              </Select>

              {imagePreset !== IMAGE_OFF && imageNeedsModel && (
                <Input
                  id="provider-image-model"
                  value={imageModel}
                  onChange={(e) => setImageModel(e.target.value)}
                  placeholder={imagePresetDef?.model ?? "image model id"}
                  spellCheck={false}
                  autoComplete="off"
                  disabled={isLoading}
                  className="h-8 text-xs"
                />
              )}

              {imagePreset !== IMAGE_OFF && imageNeedsKey && (
                <PasswordInput
                  id="provider-image-api-key"
                  value={imageApiKey}
                  onChange={(e) => setImageApiKey(e.target.value)}
                  placeholder="Empty = reuse the chat API key"
                  spellCheck={false}
                  autoComplete="off"
                  disabled={isLoading}
                  className="h-8 text-xs"
                />
              )}

              {imagePreset !== IMAGE_OFF &&
                (imagePreset === IMAGE_CUSTOM || imagePresetDef?.editableUrl) && (
                  <Input
                    id="provider-image-base-url"
                    value={imageBaseUrl}
                    onChange={(e) => setImageBaseUrl(e.target.value)}
                    placeholder={
                      imagePreset === IMAGE_CUSTOM
                        ? "Empty = same as chat provider"
                        : imagePresetDef?.baseUrl
                    }
                    spellCheck={false}
                    autoComplete="off"
                    disabled={isLoading}
                    className="h-8 text-xs"
                  />
                )}
            </div>
          )}

          {saveError && (
            <p className="m-0 text-[0.625rem] leading-snug text-destructive">
              {saveError}
            </p>
          )}

          <Button
            type="button"
            size="sm"
            className="mt-1 h-8 text-xs"
            onClick={handleSave}
            disabled={isLoading}
          >
            {catalogReady && model.trim() ? "Save" : "Save & connect"}
          </Button>

          {/* Display preference — purely local to this taskpane */}
          <div className="mt-2 flex flex-col gap-1.5 border-t border-border pt-3">
            <Label className="text-xs">Chat display</Label>
            <div
              className="flex rounded-lg border border-input bg-muted p-0.5"
              role="radiogroup"
              aria-label="Chat display style"
            >
              {(["bubbles", "flat"] as const).map((style) => (
                <button
                  key={style}
                  type="button"
                  role="radio"
                  aria-checked={renderStyle === style}
                  onClick={() => setRenderStyle(style)}
                  className={cn(
                    "flex-1 rounded-md px-2 py-1 text-[0.6875rem] transition-colors",
                    renderStyle === style
                      ? "bg-card text-foreground shadow-sm"
                      : "text-muted-foreground hover:text-foreground",
                  )}
                >
                  {style === "bubbles" ? "Bubbles" : "Flat"}
                </button>
              ))}
            </div>

          </div>
        </div>
      </SheetContent>
    </Sheet>
  )
})
