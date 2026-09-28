import { useCallback, useEffect, useRef } from 'react'
import { Message } from '@arco-design/web-react'
import { isAxiosError } from 'axios'
import { useNavigate } from 'react-router-dom'

/** 已加载的项目在后续刷新中返回 403/404 时的状态码；其他错误返回 undefined。 */
export function projectAccessLostStatus(error: unknown): 403 | 404 | undefined {
  const status = isAxiosError(error) ? error.response?.status : undefined
  return status === 403 || status === 404 ? status : undefined
}

/**
 * 页面已成功展示过项目数据后，刷新（实时信号、断线轮询或手动刷新）得到 403/404 时，
 * 提示一次并替换跳转回项目列表，避免停留在空白或“加载失败”页面。
 * 返回的函数在已处理（或组件已卸载）时返回 true，调用方据此跳过本地的错误状态。
 */
export function useProjectAccessLost(label: '主项目' | '子项目') {
  const navigate = useNavigate()
  // navigate 的引用会随路由变化；用 ref 持有，使返回的回调稳定，不触发调用方的重新请求。
  const navigateRef = useRef(navigate)
  useEffect(() => { navigateRef.current = navigate })
  const handled = useRef(false)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  return useCallback((status: 403 | 404) => {
    if (!mounted.current) return true
    if (handled.current) return true
    handled.current = true
    Message.warning(status === 404
      ? `该${label}已被删除`
      : `该${label}负责人已变更或权限已调整，您已无权访问`)
    navigateRef.current('/projects', { replace: true })
    return true
  }, [label])
}
