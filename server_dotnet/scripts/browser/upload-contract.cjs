const crypto = require('node:crypto');
const {
  assert, s, api,
} = require(process.env.YF_BROWSER_SUPPORT_DIR + '/ui-lib.cjs');

const FINGERPRINT_SAMPLE_SIZE = 1024 * 1024;
const FIXTURE_LAST_MODIFIED = 1700000000000;

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function fileFingerprint(fileName, bytes, fileLastModified = FIXTURE_LAST_MODIFIED) {
  const first = bytes.subarray(0, Math.min(FINGERPRINT_SAMPLE_SIZE, bytes.length));
  const last = bytes.subarray(Math.max(0, bytes.length - FINGERPRINT_SAMPLE_SIZE));
  const canonical = JSON.stringify([
    'yf-upload-fingerprint-v2',
    fileName,
    bytes.length,
    fileLastModified,
    sha256(first),
    sha256(last),
  ]);
  return sha256(Buffer.from(canonical, 'utf8'));
}

// 公司内部发给供应商的其它文件要求子项目已有动作流程 Excel 与 STEP/压缩文件（上传资料要求契约-2026-09-24）。
// 夹具默认不做任何补救：规则拒绝会原样抛出。只有显式传 { ensureStep: true } 的调用方（内部账号向可能还没有
// 这些资料的子项目上传其它夹具）才会在遇到该拒绝时先补传一个 STEP 和一个 Excel 文件再重试；
// 规则本身由 HTTP 契约与浏览器业务流程显式验证。
const MATERIAL_REQUIRED_MARKER = '需要同时包含动作流程 Excel';
const COMPANY_STEP_FIXTURE_NAME = 'fixture-assembly.step';
const COMPANY_STEP_FIXTURE_BYTES = Buffer.from('ISO-10303-21; fixture assembly');
const COMPANY_WORKBOOK_FIXTURE_NAME = 'fixture-motion-flow.xlsx';
const COMPANY_WORKBOOK_FIXTURE_BYTES = Buffer.from('fixture motion flow workbook');

async function initUpload(
  context, token, projectId, fileName, bytes, fileLastModified = FIXTURE_LAST_MODIFIED, { ensureStep = false } = {},
) {
  const request = () => api(context, 'POST', '/uploads/init', {
    projectId,
    fileName,
    fileSize: bytes.length,
    fileLastModified,
    fileFingerprint: fileFingerprint(fileName, bytes, fileLastModified),
  }, token);
  try {
    return (await request()).json();
  } catch (error) {
    if (!ensureStep || !String(error && error.message).includes(MATERIAL_REQUIRED_MARKER)) throw error;
  }
  await uploadFixture(context, token, projectId, COMPANY_STEP_FIXTURE_NAME, COMPANY_STEP_FIXTURE_BYTES);
  await uploadFixture(context, token, projectId, COMPANY_WORKBOOK_FIXTURE_NAME, COMPANY_WORKBOOK_FIXTURE_BYTES);
  return (await request()).json();
}

// 上传窗口要求公司发给供应商的每一批同时有 Excel 与 STEP/压缩文件。只测上传机制的单个 .zip 场景附带这个
// 小 Excel；它的分片远小于被测文件的分片，路由拦截用 isCompanionChunk 放行它，只控制被测文件。
const COMPANION_CHUNK_MAX_BYTES = 512;

function companyBatchWorkbook(name) {
  return {
    name: name.replace(/\.zip$/, '') + '-动作流程.xlsx',
    mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    buffer: Buffer.from('company batch workbook|' + name),
  };
}

function isCompanionChunk(request) {
  return (request.postDataBuffer()?.length ?? 0) <= COMPANION_CHUNK_MAX_BYTES;
}

async function putChunk(context, token, sessionId, index, chunk, label = '') {
  const response = await context.request.fetch(
    s.base + '/api/v1/uploads/' + sessionId + '/chunks/' + index,
    {
      method: 'PUT',
      data: chunk,
      headers: {
        Origin: s.base,
        Authorization: 'Bearer ' + token,
        'Content-Type': 'application/octet-stream',
        'X-Chunk-SHA256': sha256(chunk),
      },
    },
  );
  assert.equal(response.status(), 200, 'upload chunk ' + index + (label ? ' for ' + label : ''));
  return response;
}

async function uploadFixture(context, token, projectId, fileName, bytes, { ensureStep = false } = {}) {
  const initialized = await initUpload(
    context, token, projectId, fileName, bytes, FIXTURE_LAST_MODIFIED, { ensureStep },
  );
  for (let index = 0; index < initialized.totalChunks; index += 1) {
    const start = index * initialized.chunkSize;
    await putChunk(
      context,
      token,
      initialized.sessionId,
      index,
      bytes.subarray(start, Math.min(start + initialized.chunkSize, bytes.length)),
      fileName,
    );
  }
  await api(context, 'POST', '/uploads/' + initialized.sessionId + '/md5', {
    fileMd5: crypto.createHash('md5').update(bytes).digest('hex'),
  }, token);
  const merged = await (await api(
    context, 'POST', '/uploads/' + initialized.sessionId + '/merge', undefined, token,
  )).json();
  return { ...merged, sessionId: initialized.sessionId };
}

// Submission needs supplier-returned material confirmed by the project's responsible user.
async function approveMaterial(reviewerContext, reviewerToken, projectId) {
  const project = await (await api(reviewerContext, 'GET', '/projects/' + projectId, undefined, reviewerToken)).json();
  return (await api(reviewerContext, 'POST', '/projects/' + projectId + '/materials/approve', {
    expectedSupplierFileId: project.latestSupplierFileId,
  }, reviewerToken)).json();
}

async function returnApprovedMaterial(supplierContext, supplierToken, reviewerContext, reviewerToken, projectId, fileName) {
  const file = await uploadFixture(
    supplierContext, supplierToken, projectId, fileName, Buffer.from('supplier returned material|' + fileName));
  await approveMaterial(reviewerContext, reviewerToken, projectId);
  return file;
}

module.exports = {
  FIXTURE_LAST_MODIFIED,
  approveMaterial,
  companyBatchWorkbook,
  isCompanionChunk,
  fileFingerprint,
  initUpload,
  putChunk,
  returnApprovedMaterial,
  sha256,
  uploadFixture,
};
