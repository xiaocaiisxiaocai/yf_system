import { useRef, useState } from 'react'
import { Alert, Button, Form, Input, Message, Modal, Radio, Select, Space } from '@arco-design/web-react'
import { requestErrorMessage } from '../api/requestError'
import { managementApi, type DraftValues, type ManageDetail, type ManageOptions } from './managementApi'
import { internalAudience } from './managementLabels'

interface Props {
  source?: ManageDetail
  copy: boolean
  options: ManageOptions
  onClose: () => void
  onSaved: () => void
}

export default function AnnouncementEditor({ source, copy, options, onClose, onSaved }: Props) {
  const [values, setValues] = useState<DraftValues>(() => ({
    title: source?.title ?? '', body: source?.body ?? '', internalScope: source?.internalScope ?? 'OWN',
    externalScope: source?.externalScope ?? null,
    departmentIds: source?.departments.map(item => item.id) ?? [], companyIds: source?.companies.map(item => item.id) ?? [],
  }))
  const [preview, setPreview] = useState(false)
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const lock = useRef(false)
  const saved = useRef<ManageDetail | undefined>(copy ? undefined : source)
  const savedPayload = useRef('')
  const [issuer, setIssuer] = useState(options.departmentName)
  const [issuerId, setIssuerId] = useState(options.departmentId)
  const departments = options.departments.filter(item => values.departmentIds.includes(item.id))
  const companies = values.externalScope === 'ALL' ? options.companies : options.companies.filter(item => values.companyIds.includes(item.id))
  const missingDepartments = values.departmentIds.filter(id => !departments.some(item => item.id === id))
  const missingCompanies = values.companyIds.filter(id => !options.companies.some(item => item.id === id))
  const emptyCompanies = companies.filter(item => item.accountCount === 0)
  const change = (fields: Partial<DraftValues>) => {
    setError('')
    setValues(current => ({ ...current, ...fields }))
  }
  const validation = () => {
    if (!values.title.trim() || !values.body.trim()) return '请填写公告标题和正文'
    if (!issuerId) return '请先确定发布部门，再发布公告'
    if (values.internalScope === 'SELECTED' && (!departments.length || missingDepartments.length)) return '请选择有效的内部部门'
    if (!values.externalScope) return '请选择全部或指定 ODM/OEMA 公司'
    if (!companies.length || (values.externalScope === 'SELECTED' && missingCompanies.length)) return '至少需要选择一家启用的 ODM/OEMA 公司'
    return ''
  }
  const showPreview = () => {
    const reason = validation()
    setError(reason)
    if (!reason) setPreview(true)
  }
  const save = async (publish: boolean) => {
    if (lock.current) return
    const reason = publish ? validation() : ''
    if (reason) { setError(reason); return }
    lock.current = true
    setSaving(true)
    setError('')
    try {
      const draft = {
        ...values, title: values.title.trim(), body: values.body.trim(),
        departmentIds: values.internalScope === 'SELECTED' ? values.departmentIds : [],
        companyIds: values.externalScope === 'SELECTED' ? values.companyIds : [],
      }
      const serialized = JSON.stringify(draft)
      let detail = saved.current
      if (!detail || serialized !== savedPayload.current) {
        detail = detail
          ? await managementApi.update(detail.id, { ...draft, version: detail.version })
          : await managementApi.create(draft)
        saved.current = detail
        savedPayload.current = serialized
      }
      if (publish && (detail.departmentId !== issuerId || detail.departmentName !== issuer)) {
        setIssuerId(detail.departmentId)
        setIssuer(detail.departmentName)
        setError('发布部门已变化，请重新核对署名和内部范围后确认发布')
        return
      }
      if (publish) await managementApi.publish(detail.id, detail.version)
      Message.success(publish ? '公告已发布' : '草稿已保存')
      onSaved()
    } catch (e) {
      setError(requestErrorMessage(e, publish ? '发布失败，请核对草稿后重试' : '保存失败，请重试'))
    } finally {
      lock.current = false
      setSaving(false)
    }
  }
  const cancel = () => { if (!lock.current) onClose() }
  const title = preview ? '发布前预览' : copy ? '复制为新公告' : source ? '编辑公告草稿' : '新建公告'
  return <Modal className="announcement-dialog" title={title} visible onCancel={cancel} style={{ width: 'min(720px, 96vw)' }}
    maskClosable={false} escToExit={!saving} closable={!saving}
    footer={preview
      ? <Space><Button disabled={saving} onClick={() => { setPreview(false); setError('') }}>返回编辑</Button>
        <Button type="primary" loading={saving} onClick={() => void save(true)}>确认发布</Button></Space>
      : <Space><Button disabled={saving} onClick={cancel}>取消</Button>
        <Button disabled={saving} onClick={() => void save(false)}>保存草稿</Button>
        <Button type="primary" disabled={saving} onClick={showPreview}>预览发布</Button></Space>}>
    {error && <div role="alert" className="announcement-form-error"><Alert type="error" content={error} /></div>}
    {preview ? <>
      <h3 className="announcement-title">{values.title}</h3>
      <dl className="announcement-metadata">
        <dt>发布部门</dt><dd>{issuer}</dd>
        <dt>内部可见</dt><dd>{internalAudience(values.internalScope, issuer, departments.map(item => item.name))}</dd>
        <dt>外部可见</dt><dd><details><summary>{values.externalScope === 'ALL' ? '全部' : '指定'} ODM/OEMA 公司（{companies.length} 家）</summary>
          {companies.map(item => item.name).join('、')}</details></dd>
      </dl>
      {emptyCompanies.length > 0 && <Alert type="warning" content={'暂无可登录账号：' + emptyCompanies.map(item => item.name).join('、')} />}
      <article className="announcement-body">{values.body}</article>
    </> : <Form layout="vertical" disabled={saving}>
      <Form.Item label="发布部门"><span>{issuer || '未确定部门，可先保存草稿'}</span></Form.Item>
      <Form.Item label="公告标题" required><Input aria-label="公告标题" maxLength={200} value={values.title} onChange={title => change({ title })} /></Form.Item>
      <Form.Item label="公告正文" required><Input.TextArea aria-label="公告正文" maxLength={20000} rows={6} value={values.body} onChange={body => change({ body })} /></Form.Item>
      <Form.Item label="内部可见范围" required><Radio.Group value={values.internalScope} onChange={internalScope => change({ internalScope })}>
        <Radio value="OWN">本部门</Radio><Radio value="ALL">全体内部人员</Radio><Radio value="SELECTED">指定部门</Radio>
      </Radio.Group></Form.Item>
      {values.internalScope === 'SELECTED' && <Form.Item label="指定部门" required>
        <Select mode="multiple" showSearch allowClear aria-label="指定部门" placeholder="选择部门（含下属课别）"
          options={[...options.departments.map(item => ({ value: item.id, label: item.name })),
            ...missingDepartments.map(id => ({ value: id, label: (source?.departments.find(item => item.id === id)?.name ?? id) + '（已停用）' }))]}
          value={values.departmentIds} onChange={departmentIds => change({ departmentIds })} />
      </Form.Item>}
      <Form.Item label="外部可见范围" required><Radio.Group value={values.externalScope ?? ''} onChange={externalScope => change({ externalScope })}>
        <Radio value="ALL">全部 ODM/OEMA 公司</Radio><Radio value="SELECTED">指定 ODM/OEMA 公司</Radio>
      </Radio.Group></Form.Item>
      {values.externalScope === 'SELECTED' && <Form.Item label="接收公司" required>
        <Select mode="multiple" showSearch allowClear aria-label="接收公司" placeholder="搜索并选择 ODM/OEMA 公司"
          options={[...options.companies.map(item => ({ value: item.id, label: item.accountCount ? item.name : item.name + '（暂无可登录账号）' })),
            ...missingCompanies.map(id => ({ value: id, label: (source?.companies.find(item => item.id === id)?.name ?? id) + '（已停用）' }))]}
          value={values.companyIds} onChange={companyIds => change({ companyIds })} />
      </Form.Item>}
    </Form>}
  </Modal>
}
