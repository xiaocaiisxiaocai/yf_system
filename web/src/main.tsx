import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ConfigProvider } from '@arco-design/web-react'
import '@arco-design/web-react/dist/css/arco.css'
import './index.css'
import App from './App'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConfigProvider componentConfig={{
      Popconfirm: {
        position: 'tr',
        triggerProps: {
          autoFitPosition: true,
          boundaryDistance: { left: 16, top: 16 },
          updateOnScroll: true,
        },
      },
    }}>
      <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <App />
      </BrowserRouter>
    </ConfigProvider>
  </React.StrictMode>
)
