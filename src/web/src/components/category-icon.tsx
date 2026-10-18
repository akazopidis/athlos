import { Activity, BicepsFlexed, Dumbbell, Flame, Footprints, PersonStanding, StretchHorizontal, Weight, type LucideProps } from 'lucide-react'

// A program category's icon, for generated covers.
export function CategoryIcon({ category, ...props }: { category?: string | null } & LucideProps) {
  switch (category) {
    case 'Bodybuilding':
      return <BicepsFlexed {...props} />
    case 'Powerlifting':
      return <Dumbbell {...props} />
    case 'CrossFit':
      return <Flame {...props} />
    case 'Weightlifting':
      return <Weight {...props} />
    case 'Calisthenics':
      return <PersonStanding {...props} />
    case 'Running':
      return <Footprints {...props} />
    case 'Mobility':
      return <StretchHorizontal {...props} />
    default:
      return <Activity {...props} />
  }
}
