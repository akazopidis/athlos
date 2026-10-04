// Money is integer minor units plus a currency code (CLAUDE.md); this turns it into display text.
// The generated client types int64 as number | string, so both are accepted.
export function formatMoney(amount: number | string, currency: string, locale?: string): string {
  const format = new Intl.NumberFormat(locale, { style: 'currency', currency })
  const digits = format.resolvedOptions().maximumFractionDigits ?? 2
  return format.format(Number(amount) / 10 ** digits)
}
