import { useCallback, useRef, useState } from 'react'
import { Alert, Button, Form, Input, Message, Modal, Radio, Select, Space } from '@arco-design/web-react'
import { requestErrorMessage } from '../api/requestError'
import { managementApi, type DraftValues, type ManageDetail, type ManageOptions } from './managementApi'
import { internalAudience } from './managementLabels'
import RichAnnouncementEditor from './RichAnnouncementEditor'
import AnnouncementRichBody from './AnnouncementRichBody'
import AnnouncementAttachmentsEditor, { type AttachmentEntry } from './AnnouncementAttachmentsEditor'
import { announcementUploads, validateAnnouncementFile, type AnnouncementAsset, type UploadKind } from './announcementUpload'
import { loadAnnouncementAsset } from './announcementAssetContent'
import { newClientUuid } from '../utils/clientUuid'
import { documentHasImage, matchesSavedDraft, type DraftPayload } from './announcementDraft'

interface Props {
  source?: ManageDetail
  copy: boolean
  options: ManageOptions
  onClose: () => void
  onSaved: () => void
}

export default function AnnouncementEditor({ source, copy, options, onClose, onSaved }: Props) {
  const [values, setValues] = useState<DraftValues>(() => ({
    title: source?.title ?? '', body: source?.body ?? '', documentJson: source?.documentJson ?? null, internalScope: source?.internalScope ?? 'OWN',
    externalScope: source?.externalScope ?? null,
    departmentIds: source?.departments.map(item => item.id) ?? [], companyIds: source?.companies.map(item => item.id) ?? [],
  }))
  const [preview, setPreview] = useState(false)
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const lock = useRef(false)
  const saved = useRef<ManageDetail | undefined>(copy ? undefined : source)
  const creating = useRef<Promise<ManageDetail> | null>(null)
  const createKey = useRef<string | null>(null)
  const savedPayload = useRef('')
  const uncertainUpdate = useRef<{ version: number; draft: DraftPayload } | null>(null)
  const [assetOwnerId, setAssetOwnerId] = useState(source?.id ?? null)
  const [images, setImages] = useState<AnnouncementAsset[]>(() => source?.assets?.filter(a => a.kind === 'IMAGE') ?? [])
  const [attachments, setAttachments] = useState<AttachmentEntry[]>(() => source?.assets?.filter(a => a.kind === 'ATTACHMENT')
    .map(asset => ({ key: 'asset-' + asset.id, status: 'ready' as const, asset })) ?? [])
  const [unfinishedImages, setUnfinishedImages] = useState(0)
  const readyAttachments = attachments.flatMap(entry => entry.status === 'ready' ? [entry.asset] : [])
  const unfinishedFiles = unfinishedImages + attachments.length - readyAttachments.length
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
  function payload(): DraftPayload {
    return {
      ...values, title: values.title.trim(), body: values.body.trim(),
      attachmentIds: readyAttachments.map(asset => asset.id),
      departmentIds: values.internalScope === 'SELECTED' ? values.departmentIds : [],
      companyIds: values.externalScope === 'SELECTED' ? values.companyIds : [],
    }
  }
  // Concurrent paste/drop/attachment uploads share one draft. A failed upload never discards a created draft.
  function ensureDraft(draft: DraftPayload): Promise<ManageDetail> {
    if (saved.current) return Promise.resolve(saved.current)
    if (!creating.current) {
      const isRetry = createKey.current !== null
      createKey.current ??= newClientUuid()
      creating.current = managementApi.create({ ...draft, createKey: createKey.current, copyFromId: copy ? source?.id : null }).then(result => {
        if (result.status !== 'DRAFT') throw new Error('该草稿已发布或下架，请关闭编辑窗口后刷新')
        if (isRetry && result.version > 1) throw new Error('草稿已在其他窗口变更，请重新打开后编辑')
        saved.current = result
        // A retry may have recovered the earlier version after its response was lost.
        savedPayload.current = isRetry ? '' : JSON.stringify(draft)
        setAssetOwnerId(result.id)
        return result
      }).finally(() => { creating.current = null })
    }
    return creating.current
  }
  async function upload(kind: UploadKind, file: File, key: string, signal: AbortSignal, progress: (percent: number) => void) {
    validateAnnouncementFile(file, kind)
    const draft = await ensureDraft(payload())
    signal.throwIfAborted()
    return announcementUploads.send(draft.id, kind, file, key, signal, progress)
  }
  const loadImage = useCallback((id: number, signal: AbortSignal) => {
    const owner = saved.current?.id ?? source?.id
    if (!owner) return Promise.reject(new Error('公告草稿尚未建立'))
    return loadAnnouncementAsset(owner, id, signal, { thumbnail: true })
  }, [source?.id])
  const validation = () => {
    if (unfinishedFiles) return '请先完成上传，或移除未完成的文件'
    if (!values.title.trim()) return '请填写公告标题'
    if (!values.body.trim() && !readyAttachments.length && !documentHasImage(values.documentJson)) return '请填写正文、插入图片或添加附件'
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
      const draft = payload()
      const serialized = JSON.stringify(draft)
      let detail = await ensureDraft(draft)
      if (uncertainUpdate.current) {
        const sent = uncertainUpdate.current
        detail = await managementApi.detail(detail.id, new AbortController().signal)
        if (detail.status !== 'DRAFT') throw new Error('草稿状态已变化，请重新打开')
        if (detail.version !== sent.version) {
          if (detail.version !== sent.version + 1 || !matchesSavedDraft(detail, sent.draft))
            throw new Error('草稿已在其他窗口变更，当前编辑内容已保留，请先核对')
          savedPayload.current = JSON.stringify(sent.draft)
        }
        saved.current = detail
        uncertainUpdate.current = null
      }
      if (serialized !== savedPayload.current) {
        try {
          detail = await managementApi.update(detail.id, { ...draft, version: detail.version })
        } catch (cause) {
          const status = (cause as { response?: { status?: number } } | null)?.response?.status
          if (status === undefined || status === 408 || status >= 500) uncertainUpdate.current = { draft, version: detail.version }
          throw cause
        }
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
      if (!publish && unfinishedFiles) {
        Message.success('已保存草稿，未完成的文件可继续上传或移除')
        return
      }
      Message.success(publish ? '公告已发布' : '草稿已保存')
      onSaved()
    } catch (e) {
      setError(requestErrorMessage(e, publish ? '发布失败，请核对草稿后重试' : '保存失败，请重试'))
    } finally {
      lock.current = false
      setSaving(false)
    }
  }
  const cancel = () => {
    if (lock.current) return
    if (creating.current) {
      lock.current = true
      setSaving(true)
      void creating.current.then(onSaved, onClose)
    } else if (saved.current && (copy || !source)) onSaved()
    else onClose()
  }
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
      <AnnouncementRichBody announcementId={assetOwnerId} access="manage" body={values.body} documentJson={values.documentJson}
        assets={[...images, ...readyAttachments]} />
    </> : null}
    <div hidden={preview}><Form layout="vertical" disabled={saving}>
      <Form.Item label="发布部门"><span>{issuer || '未确定部门，可先保存草稿'}</span></Form.Item>
      <Form.Item label="公告标题" required><Input aria-label="公告标题" maxLength={200} value={values.title} onChange={title => change({ title })} /></Form.Item>
      <Form.Item label="公告正文">
        <RichAnnouncementEditor documentJson={values.documentJson} legacyBody={values.body} disabled={saving}
          onChange={(documentJson, body) => change({ documentJson, body })} onUnfinishedChange={setUnfinishedImages} loadImage={loadImage}
          uploadImage={async (file, progress, signal, key) => {
            const asset = await upload('IMAGE', file, key, signal, progress)
            if (!signal.aborted) setImages(current => current.some(a => a.id === asset.id) ? current : [...current, asset])
            return asset.id
          }} />
      </Form.Item>
      <Form.Item label="附件">
        <AnnouncementAttachmentsEditor announcementId={assetOwnerId} entries={attachments} setEntries={setAttachments} disabled={saving}
          upload={(file, key, signal, progress) => upload('ATTACHMENT', file, key, signal, progress)} />
      </Form.Item>
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
    </Form></div>
  </Modal>
}
