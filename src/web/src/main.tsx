import { MsalProvider } from '@azure/msal-react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { createApi } from '@/api/client'
import App from '@/app/App'
import { AppContext } from '@/lib/AppContext'
import { createMsal } from '@/lib/auth'
import { CartProvider } from '@/lib/CartProvider'
import { loadConfig } from '@/lib/config'
import './index.css'

const config = await loadConfig()
const msal = await createMsal(config)
const services = { config, msal, api: createApi(msal, config.apiScope) }
const queryClient = new QueryClient()

// Without Entra settings (for example before it is configured) the app still runs, signed out.
const withAuth = (children: ReactNode) => (msal ? <MsalProvider instance={msal}>{children}</MsalProvider> : children)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {withAuth(
      <AppContext.Provider value={services}>
        <QueryClientProvider client={queryClient}>
          <CartProvider>
            <BrowserRouter>
              <App />
            </BrowserRouter>
          </CartProvider>
        </QueryClientProvider>
      </AppContext.Provider>,
    )}
  </StrictMode>,
)
