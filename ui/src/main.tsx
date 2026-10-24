import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import '@fontsource/instrument-sans/latin-400.css'
import '@fontsource/instrument-sans/latin-500.css'
import '@fontsource/instrument-sans/latin-600.css'
import '@fontsource/instrument-sans/latin-700.css'
import '@fontsource-variable/martian-mono/wdth.css'
import '../../brand/tokens/maldex-tokens.css'
import './index.css'
import App from './App.tsx'
import { getAuthMode, getMsal, initAuth } from './auth/auth.ts'

async function start() {
  await initAuth()
  const app = <App />
  createRoot(document.getElementById('root')!).render(
    <StrictMode>{getAuthMode() === 'entra' ? <MsalProvider instance={getMsal()}>{app}</MsalProvider> : app}</StrictMode>,
  )
}

start().catch((e: unknown) => {
  document.body.textContent = `Failed to start: ${e instanceof Error ? e.message : String(e)}`
})
