import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { createAuth } from './auth'

// MSAL itself is replaced: the tests check when it is loaded, and what Auth does with it.
const { instance, createMsal, getAccessToken } = vi.hoisted(() => {
  const instance = {
    getActiveAccount: vi.fn(),
    loginRedirect: vi.fn(async () => {}),
    logoutRedirect: vi.fn(async () => {}),
  }
  return { instance, createMsal: vi.fn(async () => instance), getAccessToken: vi.fn(async () => 'token') }
})
vi.mock('./msal.ts', () => ({ createMsal, getAccessToken }))

const config = { entraClientId: 'client', entraAuthority: 'https://login.example/tenant', apiScope: 'api://athlos/access', version: 'test' }

beforeEach(() => {
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
  vi.clearAllMocks()
  instance.getActiveAccount.mockReturnValue(null)
})
afterEach(() => sessionStorage.clear())

test('a visitor who never signed in does not load MSAL', async () => {
  const auth = await createAuth(config)

  expect(auth?.account).toBeNull()
  expect(await auth?.token()).toBeNull()
  expect(createMsal).not.toHaveBeenCalled()
})

test('without Entra settings there is no sign-in', async () => {
  expect(await createAuth({ ...config, entraClientId: null })).toBeNull()
})

test('signing in loads MSAL then, and marks the tab so the way back loads it too', async () => {
  const auth = await createAuth(config)

  await auth?.signIn('/studio/new')

  expect(instance.loginRedirect).toHaveBeenCalledWith({ scopes: [config.apiScope], redirectStartPage: '/studio/new' })
  expect(sessionStorage.getItem('athlos.signedIn')).toBe('1')
})

test("Entra's answer in the address loads MSAL, which signs the visitor in", async () => {
  window.history.replaceState(null, '', '/#code=abc&state=xyz')
  instance.getActiveAccount.mockReturnValue({ username: 'anna@example.com', localAccountId: 'anna', name: 'Anna' })

  const auth = await createAuth(config)

  expect(createMsal).toHaveBeenCalledOnce()
  expect(auth?.account).toEqual({ username: 'anna@example.com', localAccountId: 'anna', name: 'Anna' })
  expect(await auth?.token()).toBe('token')
  expect(sessionStorage.getItem('athlos.signedIn')).toBe('1')
})

test('a tab that signed in loads MSAL on every page; when the account is gone the mark goes too', async () => {
  sessionStorage.setItem('athlos.signedIn', '1')

  const auth = await createAuth(config)

  expect(createMsal).toHaveBeenCalledOnce()
  expect(auth?.account).toBeNull()
  expect(sessionStorage.getItem('athlos.signedIn')).toBeNull()
})

test("a tab signed in before the mark existed is found by MSAL's account list", async () => {
  sessionStorage.setItem('msal.2.account.keys', '["anna"]')
  await createAuth(config)
  expect(createMsal).toHaveBeenCalledOnce()

  sessionStorage.setItem('msal.2.account.keys', '[]')
  await createAuth(config)
  expect(createMsal).toHaveBeenCalledOnce()
})

test('signing out clears the mark', async () => {
  sessionStorage.setItem('athlos.signedIn', '1')
  instance.getActiveAccount.mockReturnValue({ username: 'anna@example.com', localAccountId: 'anna' })
  const auth = await createAuth(config)

  await auth?.signOut()

  expect(instance.logoutRedirect).toHaveBeenCalled()
  expect(sessionStorage.getItem('athlos.signedIn')).toBeNull()
})
