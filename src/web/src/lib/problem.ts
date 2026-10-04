// The title of an RFC 9457 ProblemDetails body (design §7.1), when the error has one.
export function problemTitle(error: unknown): string | undefined {
  if (typeof error === 'object' && error !== null && 'title' in error && typeof error.title === 'string') {
    return error.title
  }
  return undefined
}
