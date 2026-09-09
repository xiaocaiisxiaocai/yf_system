import type { ReactNode } from 'react'

interface AuthShellProps {
  title?: string
  children: ReactNode
}

export default function AuthShell({ title, children }: AuthShellProps) {
  return (
    <main className="auth-page">
      <div className="auth-glow auth-glow--top" aria-hidden="true" />
      <div className="auth-glow auth-glow--bottom" aria-hidden="true" />
      <section className="auth-card" {...(title ? { 'aria-labelledby': 'auth-title' } : { 'aria-label': '账号登录' })}>
        <div className="auth-brand">
          <img src="/saa-logo.svg" alt="SAA" />
          <span>供应商协作平台</span>
        </div>
        {title && <h1 className="auth-heading" id="auth-title">{title}</h1>}
        {children}
      </section>
    </main>
  )
}
