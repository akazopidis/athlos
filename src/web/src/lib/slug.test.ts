import { expect, test } from 'vitest'
import { slugFrom } from './slug.ts'

test.each([
  ['Anna’s Strength Lab', 'annas-strength-lab'],
  ['  Café Crossfit  ', 'cafe-crossfit'],
  ['Ελληνικό Γυμναστήριο 24', '24'],
  ['a'.repeat(60), 'a'.repeat(40)],
])('slugFrom(%j) is %j', (name, slug) => {
  expect(slugFrom(name)).toBe(slug)
})
