import { afterEach, expect, test, vi } from 'vitest'
import { shrinkImage } from './image'

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

// jsdom has no image decoding or canvas, so both are stubbed: a decoded image of the given width, and a
// canvas that records what it was asked to draw and writes a 1 kB blob of the requested type.
function stubBrowser(width: number, height: number, writes = ['image/webp', 'image/jpeg']) {
  const close = vi.fn()
  vi.stubGlobal('createImageBitmap', vi.fn(async () => ({ width, height, close })))
  const drawn: number[][] = []
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
    drawImage: (_: unknown, ...box: number[]) => drawn.push(box),
    fillRect: vi.fn(),
  } as unknown as CanvasRenderingContext2D)
  vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((done, type) =>
    done(new Blob([new Uint8Array(1024)], { type: writes.includes(type ?? '') ? type : 'image/png' })),
  )
  return { drawn, close }
}

const photo = (bytes: number) => new File([new Uint8Array(bytes)], 'photo.jpg', { type: 'image/jpeg' })

test('a large photo is scaled to the width and written as WebP', async () => {
  const { drawn, close } = stubBrowser(4000, 3000)

  const image = await shrinkImage(photo(3_000_000), 1600)

  expect(image.type).toBe('image/webp')
  expect(image.size).toBe(1024)
  expect(drawn).toEqual([[0, 0, 1600, 1200]])
  expect(close).toHaveBeenCalled()
})

test('a browser that cannot write WebP sends JPEG', async () => {
  stubBrowser(4000, 3000, ['image/jpeg'])

  expect((await shrinkImage(photo(3_000_000), 1600)).type).toBe('image/jpeg')
})

test('a small image goes up unchanged', async () => {
  const { drawn } = stubBrowser(800, 600)
  const file = photo(100_000)

  expect(await shrinkImage(file, 1600)).toBe(file)
  expect(drawn).toEqual([])
})

test('an image the browser cannot read goes up as it is', async () => {
  vi.stubGlobal('createImageBitmap', vi.fn(async () => Promise.reject(new Error('not an image'))))
  const file = photo(3_000_000)

  expect(await shrinkImage(file, 1600)).toBe(file)
})
