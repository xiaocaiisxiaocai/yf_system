import { Button, Empty, Progress, Space, Spin, Tag, Typography } from '@arco-design/web-react'
import type { ProjectCopyJob } from '../../api/types'
import { fmtSize } from '../../api/types'
import './ProjectCopyJobsPanel.css'

const STATUS_META: Record<ProjectCopyJob['status'], { text: string; color: string }> = {
  pending: { text: '排队中', color: 'gray' },
  running: { text: '复制进行中', color: 'arcoblue' },
  succeeded: { text: '已完成', color: 'green' },
  failed: { text: '失败', color: 'red' },
}

function progressPercent(job: ProjectCopyJob): number {
  if (job.status === 'succeeded') return 100
  if (job.bytesTotal > 0) return Math.min(100, Math.round(job.bytesCopied / job.bytesTotal * 100))
  if (job.filesTotal > 0) return Math.min(100, Math.round(job.filesCopied / job.filesTotal * 100))
  return 0
}

function progressLabel(job: ProjectCopyJob): string {
  if (job.bytesTotal > 0) {
    const files = job.filesTotal > 0 ? `（文件 ${job.filesCopied}/${job.filesTotal}）` : ''
    return `已复制 ${fmtSize(job.bytesCopied)} / ${fmtSize(job.bytesTotal)}${files}`
  }
  if (job.filesTotal > 0) return `文件 ${job.filesCopied}/${job.filesTotal}`
  return '正在准备复制'
}

export interface ProjectCopyJobsPanelProps {
  jobs: ProjectCopyJob[]
  loading: boolean
  error: boolean
  onRetry: () => void
  onOpenResult: (projectId: number) => void
}

export default function ProjectCopyJobsPanel({
  jobs, loading, error, onRetry, onOpenResult,
}: ProjectCopyJobsPanelProps) {
  return (
    <section className="page-card project-copy-jobs-card" aria-label="复制任务">
      <div className="project-group-section-heading">
        <div>
          <h2>复制任务</h2>
          <Typography.Text type="secondary">后台任务会继续执行，关闭弹窗或刷新页面不会取消复制。</Typography.Text>
        </div>
        <Button size="small" loading={loading} onClick={onRetry}>刷新任务</Button>
      </div>
      {error && (
        <div className="page-load-error" role="alert">
          <Typography.Text type="warning">复制任务刷新失败，当前显示上次状态。</Typography.Text>
          <Button size="small" onClick={onRetry}>重试</Button>
        </div>
      )}
      {loading && jobs.length === 0 ? (
        <div className="project-copy-jobs-loading"><Spin size={24} /></div>
      ) : jobs.length === 0 ? (
        <Empty description="暂无复制任务" />
      ) : (
        <div className="project-copy-jobs-list">
          {jobs.map((job) => {
            const meta = STATUS_META[job.status]
            return (
              <article className="project-copy-job" data-copy-job-id={job.jobId} key={job.jobId}>
                <div className="project-copy-job-heading">
                  <div>
                    <Typography.Text bold>{job.targetName}</Typography.Text>
                    <Typography.Text type="secondary" className="project-copy-job-id">任务 #{job.jobId}</Typography.Text>
                  </div>
                  <Tag color={meta.color}>{meta.text}</Tag>
                </div>
                {(job.status === 'pending' || job.status === 'running') && (
                  <>
                    <Progress percent={progressPercent(job)} showText />
                    <Typography.Text type="secondary">{progressLabel(job)}</Typography.Text>
                  </>
                )}
                {job.status === 'succeeded' && job.result && (
                  <Space className="project-copy-job-result">
                    <Typography.Text type="secondary">已复制 {job.result.copyFileCount} 个文件</Typography.Text>
                    <Button type="text" size="small" onClick={() => onOpenResult(job.result!.projectId)}>进入副本</Button>
                  </Space>
                )}
                {job.status === 'failed' && (
                  <Typography.Text type="error" role="alert">{job.error || '复制失败，请重试。'}</Typography.Text>
                )}
              </article>
            )
          })}
        </div>
      )}
    </section>
  )
}
