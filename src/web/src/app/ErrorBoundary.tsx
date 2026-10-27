import { RefreshCw } from 'lucide-react'
import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'

// What a page that crashed shows instead of a blank screen (design §8). Two cases:
// - A page's code could not be downloaded, usually because a new release replaced the files the open
//   tab still points to: reloading fetches the new version.
// - Anything else: the visitor can try the page again or go home, and the error is reported to the
//   gateway (POST /client-errors, design §6), which logs it to Application Insights: the message, the
//   stack and the page's path without its query, with no cookies or account data.
// The boundary resets when the address changes (App passes the path as its key), so the rest of the
// site keeps working.
export class ErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null }

  static getDerivedStateFromError(error: Error) {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Page crashed', error, info.componentStack)
    if (isNewRelease(error)) return
    const report = { message: error.message, stack: `${error.stack ?? ''}
${info.componentStack ?? ''}`.slice(0, 2000), path: window.location.pathname }
    navigator.sendBeacon?.('/client-errors', new Blob([JSON.stringify(report)], { type: 'application/json' }))
  }

  render() {
    const { error } = this.state
    if (!error) return this.props.children

    const newRelease = isNewRelease(error)
    return (
      <div className="container-page py-16">
        <div role="alert" className="mx-auto max-w-md space-y-4 rounded-3xl border bg-card p-8 text-center">
          <h1 className="text-2xl font-bold">{newRelease ? 'A new version of Athlos is out' : 'Something went wrong'}</h1>
          <p className="text-muted-foreground">
            {newRelease
              ? 'Reload the page to get it. Your cart is kept.'
              : 'This page could not be shown. Try again, or go back to the shop; your cart and account are safe.'}
          </p>
          <div className="flex flex-wrap justify-center gap-2">
            <Button onClick={() => (newRelease ? window.location.reload() : this.setState({ error: null }))}>
              <RefreshCw aria-hidden /> {newRelease ? 'Reload' : 'Try again'}
            </Button>
            {!newRelease && (
              <Button variant="outline" asChild>
                <a href="/">Go to the shop</a>
              </Button>
            )}
          </div>
        </div>
      </div>
    )
  }
}

// A page's code that a new release replaced: not a bug, so it is not reported.
function isNewRelease(error: Error) {
  return /dynamically imported module|Importing a module script failed|Loading chunk/i.test(error.message)
}
