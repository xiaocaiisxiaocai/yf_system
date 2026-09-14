import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ConfigProvider } from '@arco-design/web-react'
import { IconClose } from '@arco-design/web-react/icon'
import '@arco-design/web-react/dist/css/arco.css'
import './index.css'
import App from './App'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConfigProvider componentConfig={{
      Drawer: {
        closeIcon: <button type="button" className="drawer-close-button" aria-label="关闭抽屉"><IconClose /></button>,
      },
      Modal: {
        // Keep popups inside the dialog focus scope, outside its scrolling content.
        getChildrenPopupContainer: (node) => node.closest('.arco-modal-content')?.parentElement || document.body,
        closeIcon: <button type="button" className="drawer-close-button" aria-label="关闭弹窗"><IconClose /></button>,
      },
      Trigger: { updateOnScroll: true, escToClose: true },
      Popconfirm: {
        position: 'tr',
        triggerProps: {
          autoFitPosition: true,
          boundaryDistance: { left: 16, top: 16 },
          updateOnScroll: true,
        },
      },
    }}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </ConfigProvider>
  </React.StrictMode>
)
