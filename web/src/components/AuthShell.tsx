import type { ReactNode } from 'react'

interface AuthShellProps {
  title?: string
  children: ReactNode
}

export default function AuthShell({ title, children }: AuthShellProps) {
  return (
    <main className="auth-page">
      <div className="auth-page-brand" aria-label="SAA 供应商协作平台">
        <img src="/saa-logo.svg" alt="SAA" />
        <span>供应商协作平台</span>
      </div>
      <section className="auth-card" {...(title ? { 'aria-labelledby': 'auth-title' } : { 'aria-label': '账号登录' })}>
        {title && <h1 className="auth-heading" id="auth-title">{title}</h1>}
        {children}
      </section>
    </main>
  )
}
