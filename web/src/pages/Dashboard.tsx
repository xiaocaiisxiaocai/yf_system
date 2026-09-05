import { useEffect, useState } from 'react'
import { Card, Grid, List, Statistic, Tag, Typography, Empty } from '@arco-design/web-react'
import { useNavigate } from 'react-router-dom'
import http from '../api/client'
import { useAuth } from '../store/auth'
import { fmtTime } from '../api/types'

interface Summary {
  projectCount: number
  activeProjectCount: number
  pendingRounds: number
  unreadMessages: number
  recentMessages: {
    id: number
    projectId: number
    projectName?: string
    content: string
    senderName?: string
    createdAt: string
  }[]
}

export default function Dashboard() {
  const [data, setData] = useState<Summary | null>(null)
  const user = useAuth((s) => s.user)
  const nav = useNavigate()

  useEffect(() => {
    http.get('/dashboard/summary').then((r) => setData(r.data))
  }, [])

  const cards = [
    { title: '可见项目', value: data?.projectCount },
    { title: '进行中项目', value: data?.activeProjectCount },
    { title: '待我方确认轮次', value: data?.pendingRounds },
    { title: '未读留言', value: data?.unreadMessages },
  ]

  return (
    <div>
      <Typography.Title heading={5} style={{ marginTop: 0 }}>
        工作台{user ? ` · ${user.realName}` : ''}
      </Typography.Title>
      <Grid.Row gutter={[16, 16]}>
        {cards.map((c) => (
          <Grid.Col xs={12} md={6} key={c.title}>
            <Card>
              <Statistic title={c.title} value={c.value ?? '-'} />
            </Card>
          </Grid.Col>
        ))}
      </Grid.Row>
      <Card className="dashboard-latest" title="最新留言" style={{ marginTop: 16 }}>
        {data && data.recentMessages.length > 0 ? (
          <List
            dataSource={data.recentMessages}
            render={(m) => (
              <List.Item
                key={m.id}
                style={{ cursor: 'pointer' }}
                onClick={() => nav(`/projects/${m.projectId}?tab=messages`)}
                extra={<Typography.Text type="secondary">{fmtTime(m.createdAt)}</Typography.Text>}
              >
                <List.Item.Meta
                  title={
                    <span>
                      <Tag color="arcoblue" style={{ marginRight: 8 }}>
                        {m.projectName || `项目#${m.projectId}`}
                      </Tag>
                      {m.senderName}
                    </span>
                  }
                  description={m.content}
                />
              </List.Item>
            )}
          />
        ) : (
          <Empty description="暂无留言" />
        )}
      </Card>
    </div>
  )
}
