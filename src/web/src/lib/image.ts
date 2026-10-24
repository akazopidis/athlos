// Covers and logos are shown far smaller than a phone photo, and an image uploaded as is made the shop
// download about 500 kB per card. Before an upload the browser scales the image down to maxWidth and
// writes it as WebP (JPEG where the browser cannot write WebP). Small images go up unchanged; images the
// browser cannot read go up as they are, and the service answers for them.
export const CoverWidth = 1600
// The shop's cards: about 400 px wide on a desktop grid, twice that on a sharp screen (design §8).
export const CardCoverWidth = 800
export const LogoWidth = 512
const SmallEnough = 300 * 1024

export async function shrinkImage(file: File, maxWidth: number): Promise<Blob> {
  if (typeof createImageBitmap !== 'function') return file
  let bitmap: ImageBitmap
  try {
    bitmap = await createImageBitmap(file)
  } catch {
    return file
  }

  const scale = Math.min(1, maxWidth / bitmap.width)
  const canvas = document.createElement('canvas')
  canvas.width = Math.round(bitmap.width * scale)
  canvas.height = Math.round(bitmap.height * scale)
  const context = scale < 1 || file.size > SmallEnough ? canvas.getContext('2d') : null
  if (!context) {
    bitmap.close()
    return file
  }
  context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
  bitmap.close()

  let shrunk = await encode(canvas, 'image/webp')
  if (shrunk?.type !== 'image/webp') {
    // JPEG has no transparency; logos and covers sit on a light background anyway.
    context.globalCompositeOperation = 'destination-over'
    context.fillStyle = '#fff'
    context.fillRect(0, 0, canvas.width, canvas.height)
    shrunk = await encode(canvas, 'image/jpeg')
  }
  return shrunk && shrunk.size < file.size ? shrunk : file
}

const encode = (canvas: HTMLCanvasElement, type: string) =>
  new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, type, 0.85))
