import { expect, test } from 'vitest'
import { readableOn } from './color.ts'

test.each([
  ['#18181b', '#ffffff'],
  ['#1d4ed8', '#ffffff'],
  ['#e11d48', '#ffffff'],
  ['#facc15', '#14161c'],
  ['#d9f945', '#14161c'],
  ['#ffffff', '#14161c'],
  ['not a color', '#ffffff'],
])('text on %s is %s', (background, text) => {
  expect(readableOn(background)).toBe(text)
})
