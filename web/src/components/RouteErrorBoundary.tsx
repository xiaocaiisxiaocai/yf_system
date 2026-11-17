import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Button, Result, Space } from '@arco-design/web-react'
import { useLocation, useNavigate } from 'react-router-dom'
import { documentNavigation } from './documentNavigation'

/** 页面分包取不到（开发服务器重启或新版本部署后旧分包已不存在），刷新即可恢复。 */
const STALE_CHUNK = /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed/i
const RELOAD_KEY = 'yf:stale-chunk-reload'
const RELOAD_WINDOW_MS = 30_000

/** 第一次遇到时自动刷新；30 秒内再失败就显示错误页，不反复刷新。 */
function reloadOnceForStaleChunk(error: Error): boolean {
  if (!STALE_CHUNK.test(error.message)) return false
  try {
    const last = Number(sessionStorage.getItem(RELOAD_KEY))
    if (last && Date.now() - last < RELOAD_WINDOW_MS) return false
    sessionStorage.setItem(RELOAD_KEY, String(Date.now()))
  } catch {
    return false
  }
  documentNavigation.reload()
  return true
}

interface BoundaryProps {
  resetKey: string
  onBack: () => void
  children: ReactNode
}

interface BoundaryState {
  error: Error | null
  resetKey: string
}

class PageErrorBoundary extends Component<BoundaryProps, BoundaryState> {
  state: BoundaryState = { error: null, resetKey: this.props.resetKey }

  static getDerivedStateFromError(error: Error): Partial<BoundaryState> {
    return { error }
  }

  static getDerivedStateFromProps(props: BoundaryProps, state: BoundaryState): Partial<BoundaryState> | null {
    if (props.resetKey !== state.resetKey) return { error: null, resetKey: props.resetKey }
    return null
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    if (reloadOnceForStaleChunk(error)) return
    console.error(error, info)
  }

  render() {
    if (!this.state.error) return this.props.children
    return (
      <Result
        status="error"
        title="页面出错"
        subTitle="当前页面出现异常，可重新加载或返回上一页。"
        extra={(
          <Space>
            <Button type="primary" onClick={() => documentNavigation.reload()}>重新加载</Button>
            <Button onClick={this.props.onBack}>返回</Button>
          </Space>
        )}
      />
    )
  }
}

export default function RouteErrorBoundary({ children }: { children: ReactNode }) {
  const location = useLocation()
  const navigate = useNavigate()
  const resetKey = `${location.pathname}${location.search}${location.hash}`
  return (
    <PageErrorBoundary resetKey={resetKey} onBack={() => navigate(-1)}>
      {children}
    </PageErrorBoundary>
  )
}
