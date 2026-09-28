import { MsalProvider } from '@azure/msal-react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { createApi } from './api/client.ts'
import App from './App.tsx'
import { AppContext } from './AppContext.tsx'
import { createMsal } from './auth.ts'
import { loadConfig } from './config.ts'
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
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </QueryClientProvider>
      </AppContext.Provider>,
    )}
  </StrictMode>,
)
