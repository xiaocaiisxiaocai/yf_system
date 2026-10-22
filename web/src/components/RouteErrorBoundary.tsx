import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Button, Result, Space } from '@arco-design/web-react'
import { useLocation, useNavigate } from 'react-router-dom'
import { documentNavigation } from './documentNavigation'

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
