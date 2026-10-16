import type { components } from '@/api/catalog'

export type Category = components['schemas']['ProgramCategory']

// The shop's categories (design §8), in the order the header and the filters show them. GeneralFitness
// is the catch-all, so it comes last.
export const Categories: { value: Category; label: string }[] = [
  { value: 'Bodybuilding', label: 'Bodybuilding' },
  { value: 'Powerlifting', label: 'Powerlifting' },
  { value: 'CrossFit', label: 'CrossFit' },
  { value: 'Weightlifting', label: 'Weightlifting' },
  { value: 'Calisthenics', label: 'Calisthenics' },
  { value: 'Running', label: 'Running' },
  { value: 'Mobility', label: 'Mobility & yoga' },
  { value: 'GeneralFitness', label: 'General fitness' },
]

export const isCategory = (value: string | null | undefined): value is Category => Categories.some((c) => c.value === value)

export const categoryLabel = (value: string) => Categories.find((c) => c.value === value)?.label ?? value
