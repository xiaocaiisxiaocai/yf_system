import type { ReactNode } from 'react'

interface AuthShellProps {
  title?: string
  /** Corner brand beside the logo. Collaboration keeps the default. */
  brand?: string
  children: ReactNode
}

export default function AuthShell({ title, brand = '供应商协作平台', children }: AuthShellProps) {
  return (
    <main className="auth-page">
      <div className="auth-page-brand" role="img" aria-label={`SAA ${brand}`}>
        <img src="/saa-logo.svg" alt="SAA" />
        <span>{brand}</span>
      </div>
      <section className="auth-card" aria-labelledby="auth-title">
        {/* 登录页不显示标题，但页面仍需要一个一级标题供读屏导航。 */}
        {title
          ? <h1 className="auth-heading" id="auth-title">{title}</h1>
          : <h1 className="visually-hidden" id="auth-title">账号登录</h1>}
        {children}
      </section>
    </main>
  )
}
