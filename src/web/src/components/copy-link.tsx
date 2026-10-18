import { Check, Copy } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'

// A public link with a copy button, for coaches to share their storefront or a program. The link is
// the page's own address on this environment (window.location.origin).
export function CopyLink({ path, label = 'Copy link' }: { path: string; label?: string }) {
  const [copied, setCopied] = useState(false)
  const url = `${window.location.origin}${path}`

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(url)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      // No clipboard access (an old browser or an insecure page): the link is on screen to copy by hand.
    }
  }

  return (
    <div className="flex min-w-0 items-center gap-2 rounded-xl border bg-muted/40 py-1 pr-1 pl-3">
      <span className="min-w-0 flex-1 truncate font-mono text-sm" title={url}>
        {url}
      </span>
      <Button type="button" size="sm" variant="outline" onClick={copy} aria-live="polite">
        {copied ? <Check aria-hidden /> : <Copy aria-hidden />} {copied ? 'Copied' : label}
      </Button>
    </div>
  )
}
