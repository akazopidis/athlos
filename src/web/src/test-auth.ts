import type { Auth } from '@/lib/auth'

// A signed-in user for tests: the account, the access token the API client sends, and sign-in and
// sign-out that do nothing unless the test passes its own.
export const signedInAs = (username: string, token = 'token', overrides: Partial<Auth> = {}): Auth => ({
  account: { username, localAccountId: username },
  token: async () => token,
  signIn: async () => {},
  signOut: async () => {},
  ...overrides,
})
