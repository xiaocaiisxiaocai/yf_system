import { useContext, useEffect, useMemo, useRef, type ReactNode } from 'react'
import { UNSAFE_NavigationContext, useLocation, type To } from 'react-router-dom'
import { confirmUploadLeave } from './uploadLeave'

function targetPathname(to: To, current: string): string {
  const path = typeof to === 'string' ? to : to.pathname
  // 只有 search/hash 变化（或 to 未给路径）时留在当前页面。
  if (!path) return current
  return new URL(path, 'http://localhost').pathname
}

/**
 * 应用使用 BrowserRouter，没有数据路由，不能用 useBlocker；这里包装路由的 navigator：
 * 跨页面的 push（菜单、Link、按钮跳转）和 go（后退）在存在进行中的上传时先二次确认。
 * replace 多用于强制重定向（登录失效、项目无权访问），不拦；需要确认的 replace 调用方自行使用 confirmUploadLeave。
 * 浏览器自带的后退/前进按钮无法在此拦截。
 */
export default function UploadNavigationGuard({ children }: { children: ReactNode }) {
  const navigation = useContext(UNSAFE_NavigationContext)
  const { pathname } = useLocation()
  const currentPath = useRef(pathname)
  useEffect(() => { currentPath.current = pathname })
  const value = useMemo(() => {
    const { navigator } = navigation
    return {
      ...navigation,
      navigator: {
        ...navigator,
        createHref: navigator.createHref.bind(navigator),
        replace: navigator.replace.bind(navigator),
        go: (delta: number) => confirmUploadLeave(() => navigator.go(delta)),
        push: (to: To, state?: unknown, options?: unknown) => {
          const go = () => (navigator.push as (...args: unknown[]) => void)(to, state, options)
          if (targetPathname(to, currentPath.current) === currentPath.current) go()
          else confirmUploadLeave(go)
        },
      },
    }
  }, [navigation])
  return <UNSAFE_NavigationContext.Provider value={value}>{children}</UNSAFE_NavigationContext.Provider>
}
