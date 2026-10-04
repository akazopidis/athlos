import { expect, test } from 'vitest'
import { formatMoney } from './money.ts'

test('formats minor units in the currency', () => {
  expect(formatMoney(4900, 'EUR', 'en-IE')).toBe('€49.00')
  expect(formatMoney('1999', 'USD', 'en-US')).toBe('$19.99')
})

test('respects currencies without minor units', () => {
  expect(formatMoney(5000, 'JPY', 'en-US')).toBe('¥5,000')
})
