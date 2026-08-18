import type { MessageAttachment } from "@/types/chat"
import { logger } from "@/lib/utils/logger"

// ---------------------------------------------------------------------------
// Attachments → wire parts
//
// The host accepts the user's message as plain text or as OpenAI-compatible
// content parts (Phase 2 contract). Attachment conversion rules:
//   - images  → image_url parts (data URL), gated host-side by capability
//   - PDFs    → native `file` part when the model advertises the pdf input
//               modality (Google/OpenRouter shape); otherwise rendered
//               LOCALLY to per-page PNGs (pdf.js) and sent as image parts —
//               any vision model can read a PDF that way, no external parser
//               service required.
// ---------------------------------------------------------------------------

export type WirePart =
  | { type: "text"; text: string }
  | { type: "image_url"; image_url: { url: string } }
  | { type: "file"; file: { filename: string; file_data: string } }

/** Capabilities of the active model that drive conversion decisions. */
export interface AttachmentCaps {
  image: boolean
  pdf: boolean
}

/** PDFs render at most this many pages (context and payload sanity). */
export const MAX_PDF_PAGES = 10
/** Attachments beyond this size are refused (base64 inflates ~33%). */
export const MAX_ATTACHMENT_BYTES = 15 * 1024 * 1024

export const ATTACHMENT_ACCEPT = "image/*,application/pdf"

export function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as string)
    reader.onerror = () => reject(reader.error ?? new Error("read failed"))
    reader.readAsDataURL(file)
  })
}

/**
 * Render a PDF (base64 data URL) to per-page PNG data URLs, locally via
 * pdf.js — lazily imported so the parser only ships when a PDF is attached.
 */
export async function pdfToPageImages(
  dataUrl: string,
  maxPages = MAX_PDF_PAGES,
): Promise<{ images: string[]; totalPages: number }> {
  const pdfjs = await import("pdfjs-dist")
  const workerUrl = (await import("pdfjs-dist/build/pdf.worker.min.mjs?url")).default
  pdfjs.GlobalWorkerOptions.workerSrc = workerUrl

  const task = pdfjs.getDocument({ url: dataUrl })
  const doc = await task.promise
  const totalPages = doc.numPages
  const pages = Math.min(totalPages, maxPages)
  const images: string[] = []

  for (let pageNum = 1; pageNum <= pages; pageNum++) {
    const page = await doc.getPage(pageNum)
    const viewport = page.getViewport({ scale: 1.5 })
    const canvas = document.createElement("canvas")
    canvas.width = Math.ceil(viewport.width)
    canvas.height = Math.ceil(viewport.height)
    await page.render({ canvas, canvasContext: canvas.getContext("2d")!, viewport }).promise
    images.push(canvas.toDataURL("image/png"))
    page.cleanup()
  }
  void task.destroy()
  return { images, totalPages }
}
export async function buildWireContent(
  text: string,
  attachments: MessageAttachment[] | undefined,
  caps: AttachmentCaps,
): Promise<string | WirePart[]> {
  if (!attachments || attachments.length === 0) return text

  const parts: WirePart[] = []
  if (text) parts.push({ type: "text", text })

  for (const attachment of attachments) {
    if (attachment.mime === "application/pdf") {
      if (caps.pdf) {
        parts.push({
          type: "file",
          file: { filename: attachment.name, file_data: attachment.dataUrl },
        })
        continue
      }
      // Fallback: local page render → image parts (needs vision).
      if (caps.image) {
        try {
          const { images, totalPages } = await pdfToPageImages(attachment.dataUrl)
          attachment.pageImages = images
          parts.push({
            type: "text",
            text:
              `[PDF attached: ${attachment.name} — sent as ${images.length} page image(s)` +
              (totalPages > images.length ? ` of ${totalPages} pages` : "") +
              "]",
          })
          for (const image of images) {
            parts.push({ type: "image_url", image_url: { url: image } })
          }
        } catch (err) {
          logger.error("attachments: PDF render failed", { err, name: attachment.name })
          parts.push({
            type: "text",
            text: `[PDF attached: ${attachment.name} — could not be rendered locally]`,
          })
        }
        continue
      }
      // Neither native pdf nor vision — the host will drop it with a note.
      parts.push({
        type: "file",
        file: { filename: attachment.name, file_data: attachment.dataUrl },
      })
      continue
    }

    // Images (and future media) go through as-is; the host gates by capability.
    parts.push({ type: "image_url", image_url: { url: attachment.dataUrl } })
  }

  return parts
}

// ---------------------------------------------------------------------------
// History reconstruction (content_parts → display model)
// ---------------------------------------------------------------------------
export function partsToDisplay(parts: Array<Record<string, unknown>>): {
  text: string
  attachments: MessageAttachment[]
  images: string[]
} {
  const texts: string[] = []
  const attachments: MessageAttachment[] = []
  const images: string[] = []
  for (const part of parts) {
    const type = part?.type
    if (type === "text" && typeof part.text === "string") {
      texts.push(part.text)
    } else if (type === "image_url") {
      const imageUrl = (part.image_url as Record<string, unknown> | undefined)?.url
      if (typeof imageUrl === "string") images.push(imageUrl)
    } else if (type === "file") {
      const file = part.file as Record<string, unknown> | undefined
      const fileData = file?.file_data
      attachments.push({
        name: typeof file?.filename === "string" ? file.filename : "file.pdf",
        mime: "application/pdf",
        dataUrl: typeof fileData === "string" ? fileData : "",
      })
    }
  }
  return { text: texts.join("\n"), attachments, images }
}
