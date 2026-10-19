// A rating for display: one decimal ("4.5").
export function formatRating(value: number) {
  return Number(value).toFixed(1)
}
