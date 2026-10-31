// ---------------------------------------------------------------------------
// defineMessages — one file per UI area, English is the source of truth and
// Spanish must provide every key (checked at compile time).
// ---------------------------------------------------------------------------
export type Messages = Record<string, string>

export function defineMessages<const E extends Messages>(messages: {
  en: E
  es: { [K in keyof E]: string }
}) {
  return messages
}
