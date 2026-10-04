// A coach picks any brand color; text on it must stay readable. WCAG relative luminance decides
// between near-black and white, whichever contrasts more.
const Dark = '#14161c'
const Light = '#ffffff'

export function readableOn(hex: string): string {
  const match = /^#?([0-9a-f]{6})$/i.exec(hex.trim())
  if (!match) return Light
  const channel = (i: number) => {
    const c = parseInt(match[1].slice(i, i + 2), 16) / 255
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }
  const luminance = 0.2126 * channel(0) + 0.7152 * channel(2) + 0.0722 * channel(4)
  // Contrast with white is 1.05 / (L + 0.05); with near-black about (L + 0.05) / 0.059.
  return 1.05 / (luminance + 0.05) >= (luminance + 0.05) / 0.059 ? Light : Dark
}
