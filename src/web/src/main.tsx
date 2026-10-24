import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { createApi } from '@/api/client'
import App from '@/app/App'
import { AppContext } from '@/lib/AppContext'
import { createAuth } from '@/lib/auth'
import { CartProvider } from '@/lib/CartProvider'
import { loadConfig } from '@/lib/config'
import './index.css'

const config = await loadConfig()
const auth = await createAuth(config)
const services = { config, auth, api: createApi(auth) }
const queryClient = new QueryClient()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppContext.Provider value={services}>
      <QueryClientProvider client={queryClient}>
        <CartProvider>
          <BrowserRouter>
            <App />
          </BrowserRouter>
        </CartProvider>
      </QueryClientProvider>
    </AppContext.Provider>
  </StrictMode>,
)
