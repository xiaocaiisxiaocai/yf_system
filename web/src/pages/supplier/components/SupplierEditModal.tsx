import { Form, Input, Modal } from '@arco-design/web-react'
import type { FormInstance } from '@arco-design/web-react'
import { textLengthRule } from '../../../utils/textRules'
import type { Supplier } from './supplierTypes'

interface SupplierEditModalProps {
  editing: Supplier | null
  editOpen: boolean
  saving: boolean
  form: FormInstance
  onOk: () => void | Promise<void>
  onCancel: () => void
}

export function SupplierEditModal({ editing, editOpen, saving, form, onOk, onCancel }: SupplierEditModalProps) {
  return (
    <Modal
      className="form-dialog"
      title={editing ? '编辑供应商' : '新增供应商'}
      visible={editOpen}
      confirmLoading={saving}
      closable={!saving}
      maskClosable={!saving}
      escToExit={!saving}
      cancelButtonProps={{ disabled: saving }}
      okText={editing ? '保存修改' : '创建供应商'}
      onOk={onOk}
      onCancel={onCancel}
    >
      <Form form={form} layout="vertical">
        <Form.Item label="供应商名称" field="name" rules={[{ required: true, message: '请输入名称' }, textLengthRule('供应商名称', 64)]}>
          <Input placeholder="公司全称" />
        </Form.Item>
        <Form.Item label="备注" field="remark">
          <Input.TextArea rows={3} maxLength={500} placeholder="选填" />
        </Form.Item>
      </Form>
    </Modal>
  )
}
